using System.Security.Claims;
using Queryeables.Abstraction;
using Queryeables.Replication;
using Queryeables.Tests.Contract;
using Routeables.Remote;

namespace Queryeables.Tests;

/// <summary>A person as the application's own entity: what the projection writes from the replicated documents.</summary>
public sealed class ReplicatedPerson
{
    public long Id { get; set; }

    public string Key { get; set; } = "";

    public string? Name { get; set; }

    public long Visits { get; set; }
}

/// <summary>
/// The Routeables Remote Protocol on a durable server: <see cref="EntityReplicationStore"/> over a Queryeables store.
/// Devices write offline, the server process stops and starts again over the same database, and everything converges
/// without a write lost or applied twice. Each store (memory, EF Core on SQLite, MongoDB) runs the same tests.
/// </summary>
public abstract class ReplicationStoreContractTests : IAsyncLifetime
{
    public abstract Task InitializeAsync();

    public abstract Task DisposeAsync();

    /// <summary>Stores on one database: every call gives a new store, as a server does per request.</summary>
    protected abstract IEntityStoreFactory Stores { get; }

    private static readonly DocumentId Ana = new("Person", "ana");
    private static readonly DocumentId Note = new("Note", "n1");
    private static readonly string[] Collections = ["Person", "Note"];

    private static readonly ClaimsPrincipal User = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user")], "test"));

    private static IReplicationProjection People => new ReplicationProjection<ReplicatedPerson>(
        "Person",
        key => p => p.Key == key,
        (document, existing) =>
        {
            var person = existing ?? new ReplicatedPerson { Key = document.Id.Key };
            person.Name = document.Get("name")?.Trim('"');
            person.Visits = document.Counter("visits");
            return person;
        });

    private ReplicationServer StartServer() => new(store: new EntityReplicationStore(Stores, [People]));

    /// <summary>A device: its replica, its storage, and a channel that loses the next answer when asked.</summary>
    private sealed class Device(IReplicaStorage storage, Replica replica)
    {
        public IReplicaStorage Storage { get; } = storage;

        public Replica Replica { get; private set; } = replica;

        public bool LoseNextPushAnswer { get; set; }

        public static async Task<Device> StartAsync()
        {
            var storage = new MemoryReplicaStorage();
            return new Device(storage, await Replica.OpenAsync(storage, Collections));
        }

        public async Task RestartAsync() => Replica = await Replica.OpenAsync(Storage, Collections);

        public async Task<SyncReport> SyncAsync(ReplicationServer server)
            => await Replica.SyncAsync(new Lossy(server.Connect(User), this));

        public async Task<bool> TrySyncAsync(ReplicationServer server)
        {
            try
            {
                await SyncAsync(server);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private sealed class Lossy(IReplicationChannel inner, Device device) : IReplicationChannel
        {
            public Task<RemoteWelcome> HelloAsync(RemoteHello hello, CancellationToken ct = default) => inner.HelloAsync(hello, ct);

            public async Task<PushResult> PushAsync(PushRequest request, CancellationToken ct = default)
            {
                var result = await inner.PushAsync(request, ct);
                if (device.LoseNextPushAnswer)
                {
                    device.LoseNextPushAnswer = false;
                    throw new IOException("The connection was reset.");
                }
                return result;
            }

            public Task<PullResult> PullAsync(PullRequest request, CancellationToken ct = default) => inner.PullAsync(request, ct);

            public Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default) => inner.SnapshotAsync(request, ct);
        }
    }

    [ServerFact]
    public async Task Devices_converge_across_a_restart_of_the_server_without_a_write_lost_or_doubled()
    {
        var server = StartServer();
        var a = await Device.StartAsync();
        var b = await Device.StartAsync();
        await a.Replica.Edit().Create(Ana).Set(Ana, "name", "\"Ana\"").Increment(Ana, "visits", 1)
            .Create(Note).InsertText(Note, "body", 0, "The quick fox").CommitAsync();
        await a.SyncAsync(server);
        await b.SyncAsync(server);

        // Both write offline; A's push is stored but its answer lost; then the server process stops.
        await a.Replica.Edit().Increment(Ana, "visits", 2).ReplaceText(Note, "body", "The quick brown fox").CommitAsync();
        await b.Replica.Edit().Increment(Ana, "visits", 3).ReplaceText(Note, "body", "The quick fox jumps").CommitAsync();
        a.LoseNextPushAnswer = true;
        Assert.False(await a.TrySyncAsync(server));
        await a.RestartAsync();

        server = StartServer();                                // the same database, a new process
        await b.SyncAsync(server);
        await a.SyncAsync(server);
        await b.SyncAsync(server);

        var stored = await server.FindAsync(Ana);
        Assert.Equal(6, stored!.Counter("visits"));
        Assert.Equal("The quick brown fox jumps", (await server.FindAsync(Note))!.Text("body"));
        foreach (var device in new[] { a, b })
        {
            Assert.Equal(6, device.Replica.Find(Ana)!.Counter("visits"));
            Assert.Equal("The quick brown fox jumps", device.Replica.Find(Note)!.Text("body"));
            Assert.Equal(0, device.Replica.PendingCount);
        }
        Assert.Equal(3, (await server.Store.HeadAsync()).Position);

        // The projection: the application's own table, written in the same transactions.
        await using var store = Stores.Create();
        var person = Assert.Single(await store.GetAllAsync<ReplicatedPerson>());
        Assert.Equal(("ana", "Ana", 6L), (person.Key, person.Name, person.Visits));
    }

    [ServerFact]
    public async Task A_device_behind_a_compacted_log_gets_a_snapshot_from_the_database()
    {
        var server = StartServer();
        var a = await Device.StartAsync();
        await a.Replica.Edit().Create(Ana).Increment(Ana, "visits", 5).CommitAsync();
        await a.SyncAsync(server);
        var late = await Device.StartAsync();
        for (var i = 0; i < 3; i++)
        {
            await a.Replica.Edit().Increment(Ana, "visits", 1).CommitAsync();
            await a.SyncAsync(server);
        }
        await server.CompactAsync((await server.Store.HeadAsync()).Position);

        var report = await late.SyncAsync(StartServer());
        Assert.True(report.Snapshot);
        Assert.Equal(8, late.Replica.Find(Ana)!.Counter("visits"));
        Assert.Empty(await server.Store.ReadAsync(0, long.MaxValue, Collections, 100));
    }

    [ServerFact]
    public async Task A_deleted_document_deletes_its_entity_and_a_refused_change_writes_nothing()
    {
        var server = new ReplicationServer(new RefuseName("forbidden"), store: new EntityReplicationStore(Stores, [People]));
        var a = await Device.StartAsync();
        await a.Replica.Edit().Create(Ana).Set(Ana, "name", "\"Ana\"").CommitAsync();
        await a.SyncAsync(server);
        await a.Replica.Edit().Set(Ana, "name", "\"forbidden\"").CommitAsync();
        Assert.Equal(1, (await a.SyncAsync(server)).Rejected);

        await using (var store = Stores.Create())
            Assert.Equal("Ana", Assert.Single(await store.GetAllAsync<ReplicatedPerson>()).Name);
        Assert.Equal(1, (await server.Store.HeadAsync()).Position);
        Assert.Equal(2, (await server.Store.FindReplicaAsync(a.Replica.Id))!.LastSeq);    // decided, refused, remembered

        await a.Replica.Edit().Delete(Ana).CommitAsync();
        await a.SyncAsync(server);
        await using (var store = Stores.Create())
            Assert.Empty(await store.GetAllAsync<ReplicatedPerson>());
    }

    [ServerFact]
    public async Task A_pull_reads_only_the_entries_of_its_collections_in_pages()
    {
        var server = StartServer();
        var a = await Device.StartAsync();
        for (var i = 0; i < 5; i++)
            await a.Replica.Edit().Create(new DocumentId(i % 2 == 0 ? "Person" : "Note", $"d{i}")).CommitAsync();
        await a.SyncAsync(server);

        var people = await server.Store.ReadAsync(0, 5, ["Person"], 10);
        Assert.Equal([1L, 3L, 5L], people.Select(e => e.Position));
        var page = await server.Store.ReadAsync(1, 5, Collections, 2);
        Assert.Equal([2L, 3L], page.Select(e => e.Position));
    }

    private sealed class RefuseName(string name) : IReplicationPolicy
    {
        public ValueTask<Admission> AdmitAsync(ClaimsPrincipal? user, Change change, Func<DocumentId, ValueTask<DocumentState?>> current, CancellationToken ct)
            => ValueTask.FromResult(change.Ops.Any(o => o.Op is SetField { Value: var v } && v == $"\"{name}\"")
                ? Admission.Reject(RemoteErrorCodes.Invalid, "This name is not allowed.")
                : Admission.Accept);

        public bool CanRead(ClaimsPrincipal? user, DocumentId document, DocumentState? state) => true;
    }
}

/// <summary>The replication store on the in-memory test store (transactions included).</summary>
public sealed class MemoryReplicationStoreContractTests : ReplicationStoreContractTests
{
    private readonly MemoryDatabase _database = new();

    public override Task InitializeAsync() => Task.CompletedTask;

    public override Task DisposeAsync() => Task.CompletedTask;

    protected override IEntityStoreFactory Stores => new EntityStoreFactory(() => new MemoryStore(_database));
}

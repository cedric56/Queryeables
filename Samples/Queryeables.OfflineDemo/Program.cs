using Microsoft.Extensions.DependencyInjection;
using Queryeables;
using Queryeables.Abstraction;
using Queryeables.Dommel;
using Queryeables.Remote;
using Queryeables.Remote.Offline;
using Queryeables.Sqlite;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;
using System.Text.Json;

// One server and two clients, Alice and Bob, in one process: the server is called in process instead of through
// MagicOnion, and each client has a network cable that can be pulled. Everything else is what an application runs:
// SQLite on the server, caches that follow it, the offline mode with its copy in a SQLite file, conflicts.

var folder = Directory.CreateTempSubdirectory("queryeables-offline-demo").FullName;
var serializer = new JsonEntitySerializer();

// ---------- Server ----------
var serverConnection = $"Data Source={Path.Combine(folder, "server.db")}";
var serverServices = new ServiceCollection();
serverServices.UseQueryeables(q => q
    .UseSqlite(serverConnection)
    .WithSyncConventions()                                   // [LastModified], [SoftDelete]: incremental downloads
    .AddChangeFeed<Todo>()                                   // SQLite reports each change of the table
    .AddEntityServer(serializer, server => server.Expose<Todo>(EntityAccessPolicies.AllowAll)));     // in process only
await using var server = serverServices.BuildServiceProvider();
await DommelEntityStore.EnsureSchemaAsync(server.GetRequiredService<DbProviderFactory>(), serverConnection, """
    CREATE TABLE Todo (Id INTEGER PRIMARY KEY AUTOINCREMENT, Title TEXT NOT NULL, Done INTEGER NOT NULL,
                       LastModified TEXT NOT NULL, Deleted INTEGER NOT NULL, Version INTEGER NOT NULL);
    """);
var dispatcher = server.GetRequiredService<EntityRequestDispatcher>();
var serverFeed = server.GetRequiredService<IChangeFeed<Todo>>();

await using var alice = await Client.StartAsync("Alice", dispatcher, serverFeed, serializer, folder);
await using var bob = await Client.StartAsync("Bob", dispatcher, serverFeed, serializer, folder);

// ---------- 1. A change reaches the other client ----------
Step("1. Alice adds a task; Bob's cache receives it.");
var milk = new Todo { Title = "Buy milk" };
await alice.Stores.Create().UsingAsync(store => store.InsertAsync(milk));
await WaitForAsync(() => bob.Cache.Find(milk.Id) is not null, "Bob did not receive the new task.");
Show(bob);
await alice.Offline.RefreshSnapshotAsync();       // the copy is refreshed while online: an application does it regularly

// ---------- 2. Alice loses the network, and chooses to work offline ----------
Step("2. Alice's network goes down. She is told, and chooses to work offline.");
alice.Network.Up = false;
try
{
    await alice.Stores.Create().UsingAsync(store => store.GetAllAsync<Todo>());
}
catch (HttpRequestException)
{
    // The offline mode noticed: StatusChanged said ServerUnreachable.
}
var offline = await alice.Offline.GoOfflineAsync();      // no network: the copy taken at startup is used
Console.WriteLine($"   Offline: {offline.Successful}, copy taken at {alice.Offline.Status.SnapshotTakenAt:HH:mm:ss}");

// ---------- 3. Both write: Alice offline, Bob online, on the same task ----------
Step("3. Alice renames the task and adds another, offline. Bob renames the same task, online.");
await alice.Stores.Create().UsingAsync(async store =>
{
    var mine = (await store.GetAllAsync<Todo>()).Single(t => t.Id == milk.Id);
    mine.Title = "Buy oat milk";
    await store.UpdateAsync(mine);
    await store.InsertAsync(new Todo { Title = "Call the plumber" });
});
await bob.Stores.Create().UsingAsync(async store =>
{
    var his = (await store.GetAllAsync<Todo>()).Single(t => t.Id == milk.Id);
    his.Title = "Buy 2 bottles of milk";
    await store.UpdateAsync(his);
});
var onServer = await bob.Stores.Create().ReadAsync(store => store.FirstOrDefaultAsync<Todo>(t => t.Id == milk.Id));
Console.WriteLine($"   Alice: {alice.Offline.Status.PendingChanges} change(s) waiting. Server: \"{onServer?.Title}\"");
Show(alice);

// ---------- 4. The network is back: Alice is told the data differs, and chooses to synchronize ----------
Step("4. Alice's network is back. She is told what differs, and chooses to synchronize.");
var available = new TaskCompletionSource<OfflineStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
alice.Offline.StatusChanged += (_, e) =>
{
    if (e.Reason == OfflineEvent.ServerAvailable)
        available.TrySetResult(e.Status);
};
alice.Network.Up = true;
var status = await available.Task.WaitAsync(TimeSpan.FromSeconds(10));
Console.WriteLine($"   Server back: {status.PendingChanges} change(s) to send, {status.ServerChanges} changed on the server, desynchronized: {status.IsDesynchronized}");

var report = await alice.Offline.GoOnlineAsync();
Console.WriteLine($"   Sent: {report.Sent}, refused: {report.Conflicts.Count}");
foreach (var conflict in report.Conflicts)
    Console.WriteLine($"   Conflict on #{conflict.Key}: {conflict.ErrorCode}, hers: \"{alice.Offline.ReadConflict<Todo>(conflict)?.Title}\"");

// ---------- 5. Alice resolves the conflict; Bob sees the result ----------
Step("5. Alice keeps her version. Bob's cache follows.");
foreach (var conflict in alice.Offline.Conflicts)
    Check(await alice.Offline.KeepMineAsync(conflict));
await WaitForAsync(() => bob.Cache.Find(milk.Id)?.Title == "Buy oat milk" && bob.Cache.Entities.Count == 2,
    "Bob did not receive Alice's changes.");
Show(alice);
Show(bob);

Directory.Delete(folder, recursive: true);

static void Step(string text) => Console.WriteLine(Environment.NewLine + text);

static void Show(Client client)
    => Console.WriteLine($"   {client.Name} ({client.Offline.Mode}): " +
        string.Join(", ", client.Cache.Entities.OrderBy(t => t.Title).Select(t => $"#{t.Id} {t.Title}")));

static void Check(EntityResult result)
{
    if (!result.Successful)
        throw new InvalidOperationException($"{result.ErrorCode}: {result.Exception}");
}

static async Task WaitForAsync(Func<bool> condition, string failure)
{
    for (var i = 0; i < 100 && !condition(); i++)
        await Task.Delay(50);
    if (!condition())
        throw new InvalidOperationException(failure);
}

/// <summary>A task. [LastModified], [SoftDelete] and the version are kept by the server.</summary>
[Table("Todo")]
public sealed class Todo
{
    public long Id { get; set; }

    [Required]
    public string Title { get; set; } = string.Empty;

    public bool Done { get; set; }

    [LastModified]
    public DateTime LastModified { get; set; }

    [SoftDelete]
    public bool Deleted { get; set; }

    [ConcurrencyCheck]
    public long Version { get; set; }
}

/// <summary>One client: its own services, network cable, cache and offline mode, and the changes of the server it receives.</summary>
internal sealed class Client : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly ChangeRelay<Todo> _relay;
    private readonly IChangeFeed<Todo> _serverFeed;

    private Client(string name, ServiceProvider services, Network network, IChangeFeed<Todo> serverFeed)
    {
        Name = name;
        _services = services;
        Network = network;
        _serverFeed = serverFeed;

        // What a change hub does over MagicOnion or SignalR: the server's changes, except this client's own, while connected.
        _relay = new ChangeRelay<Todo>(services.GetRequiredService<IChangeFeed<Todo>>(), () => network.Up ? name : null);
        _serverFeed.ChangeReceived += OnServerChange;
    }

    public string Name { get; }

    public Network Network { get; }

    public IEntityStoreFactory Stores => _services.GetRequiredService<IEntityStoreFactory>();

    public IEntityCache<Todo, long> Cache => _services.GetRequiredService<IEntityCache<Todo, long>>();

    public OfflineMode Offline => _services.GetRequiredService<OfflineMode>();

    public static async Task<Client> StartAsync(string name, EntityRequestDispatcher dispatcher, IChangeFeed<Todo> serverFeed,
        IEntitySerializer serializer, string folder)
    {
        var network = new Network(dispatcher.AsTransport(), name);
        var services = new ServiceCollection();
        services.AddSingleton<IEntityTransport>(network);
        services.AddSingleton(serializer);
        services.UseQueryeables(q => q
            .UseStores(sp => RemoteEntityStore.Factory(network, serializer, sp.GetRequiredService<IChangeSession>()))
            .AddCache<Todo, long>(t => t.Id)
            .WithOfflineMode(o =>
            {
                o.UseStorage(new SqliteOfflineStorage($"Data Source={Path.Combine(folder, name + "-offline.db")}"));
                o.Entity<Todo, long>(t => t.Id);
                o.ProbeInterval = TimeSpan.FromMilliseconds(250);
            }));
        var provider = services.BuildServiceProvider();

        // The id under which the server publishes this client's writes: its own changes are not sent back to it.
        provider.GetRequiredService<IChangeSession>().SetSession(typeof(Todo), (name, () => Task.CompletedTask));

        var client = new Client(name, provider, network, serverFeed);
        await client.Offline.InitializeAsync();
        await client.Offline.RefreshSnapshotAsync();      // a copy while online, for when the network goes
        await client.Cache.LoadAsync();
        return client;
    }

    private void OnServerChange(object? sender, EntityChange<Todo> change)
    {
        if (Network.Up)
            _relay.Receive(change);
    }

    public async ValueTask DisposeAsync()
    {
        _serverFeed.ChangeReceived -= OnServerChange;
        _relay.Dispose();
        await _services.DisposeAsync();
    }
}

/// <summary>The network of one client: when it is down, every call fails as a lost connection does.</summary>
internal sealed class Network(IEntityTransport server, string client) : IEntityTransport
{
    public volatile bool Up = true;

    private Task<TransportResult> Call(Func<Task<TransportResult>> call)
        => Up ? call() : Task.FromException<TransportResult>(new HttpRequestException($"{client}: no network."));

    public Task<TransportResult> GetAllAsync(string type, CancellationToken ct = default) => Call(() => server.GetAllAsync(type, ct));

    public Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, bool any, CancellationToken ct = default)
        => Call(() => server.SelectAsync(type, conditions, any, ct));

    public Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        CancellationToken ct = default)
        => Call(() => server.SelectPagedAsync(type, conditions, any, pageNumber, pageSize, ct));

    public Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        SortKey[] sort, CancellationToken ct = default)
        => Call(() => server.SelectPagedAsync(type, conditions, any, pageNumber, pageSize, sort, ct));

    public Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default)
        => Call(() => server.GetChangesAsync(type, sinceUtc, ct));

    public Task<TransportResult> InsertAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
        => Call(() => server.InsertAsync(type, entity, connectionId, ct));

    public Task<TransportResult> UpdateAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
        => Call(() => server.UpdateAsync(type, entity, connectionId, ct));

    public Task<TransportResult> DeleteAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
        => Call(() => server.DeleteAsync(type, entity, connectionId, ct));

    public Task<TransportResult> ExecuteBatchAsync(BatchOperation[] operations, CancellationToken ct = default)
        => Call(() => server.ExecuteBatchAsync(operations, ct));

    public Task<TransportResult> ExecuteBatchOnceAsync(Guid operationId, BatchOperation[] operations, CancellationToken ct = default)
        => Call(() => server.ExecuteBatchOnceAsync(operationId, operations, ct));

    public Task<TransportResult> WriteOnceAsync(Guid operationId, BatchOperationKind kind, string type, byte[] entity, string? connectionId,
        CancellationToken ct = default)
        => Call(() => server.WriteOnceAsync(operationId, kind, type, entity, connectionId, ct));
}

internal static class StoreExtensions
{
    /// <summary>Reads with the store, then disposes it.</summary>
    public static async Task<T> ReadAsync<T>(this IEntityStore store, Func<IEntityStore, Task<T>> read)
    {
        await using (store)
            return await read(store);
    }

    /// <summary>Runs <paramref name="work"/> on the store, then disposes it.</summary>
    public static async Task UsingAsync(this IEntityStore store, Func<IEntityStore, Task> work)
    {
        await using (store)
            await work(store);
    }
}

/// <summary>Entities as JSON on the wire. MagicOnion uses MessagePack (MessagePackEntitySerializer) instead.</summary>
internal sealed class JsonEntitySerializer : IEntitySerializer
{
    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    public T? Deserialize<T>(byte[] data) => JsonSerializer.Deserialize<T>(data);

    /// <summary>System.Text.Json leaves out <c>[JsonIgnore]</c>: an update keeps the stored value of such a property.</summary>
    public bool Omits(System.Reflection.PropertyInfo property)
        => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>(property)
            is { Condition: System.Text.Json.Serialization.JsonIgnoreCondition.Always };
}

using Queryeables.Abstraction;
using Queryeables.Remote;
using Queryeables.Remote.Offline;

namespace Queryeables.Tests.Unit;

/// <summary>
/// The scenario of <c>Samples/Queryeables.OfflineDemo</c>, in memory: two clients of one server, a change that reaches
/// the other client, a network cut, writes on both sides, the return of the server, a conflict, and its resolution.
/// </summary>
public class TwoClientsScenarioTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();
    private readonly ChangeFeed<TestClient> _serverFeed = new();
    private readonly EntityRequestDispatcher _dispatcher;

    public TwoClientsScenarioTests()
    {
        var stores = new EntityStoreFactory(() => new MemoryStore(_database).WithSyncConventions());
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<TestClient>(stores, EntityAccessPolicies.AllowAll));
        _dispatcher = new EntityRequestDispatcher(registry, _serializer, stores);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), because);
    }

    [Fact]
    public async Task Two_clients_a_network_cut_a_conflict_and_its_resolution()
    {
        await using var alice = await Client.StartAsync("alice", this);
        await using var bob = await Client.StartAsync("bob", this);

        // 1. A change reaches the other client.
        var task = new TestClient { Name = "Buy milk" };
        await using (var store = alice.Stores.Create())
            await store.InsertAsync(task);
        await WaitUntilAsync(() => bob.Cache.Find(task.Id) is not null, "Bob did not receive the new row");
        await alice.Offline.RefreshSnapshotAsync();         // the copy is refreshed while online (an application does it regularly)

        // 2. Alice loses the network: she is told, and chooses to work offline on her copy.
        var events = new List<OfflineEvent>();
        alice.Offline.StatusChanged += (_, e) => { lock (events) events.Add(e.Reason); };
        alice.Network.Down = true;
        await using (var store = alice.Stores.Create())
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => store.GetAllAsync<TestClient>());
        Assert.Equal(ConnectionMode.Online, alice.Offline.Mode);            // told, not switched
        Assert.True((await alice.Offline.GoOfflineAsync()).Successful);

        // 3. Both write: Alice offline renames the row and adds one; Bob online renames the same row.
        await using (var store = alice.Stores.Create())
        {
            var mine = (await store.GetAllAsync<TestClient>()).Single(c => c.Id == task.Id);
            mine.Name = "Buy oat milk";
            await store.UpdateAsync(mine);
            await store.InsertAsync(new TestClient { Name = "Call the plumber" });
        }
        await using (var store = bob.Stores.Create())
        {
            var his = (await store.GetAllAsync<TestClient>()).Single(c => c.Id == task.Id);
            his.Name = "Buy 2 bottles of milk";
            await store.UpdateAsync(his);
        }
        await WaitUntilAsync(() => alice.Cache.Entities.Count == 2, "Alice's cache did not follow her offline writes");
        Assert.Equal(2, alice.Offline.Status.PendingChanges);

        // 4. The network is back: Alice is told the data differs, and chooses to synchronize.
        alice.Network.Down = false;
        await WaitUntilAsync(() => alice.Offline.Status.ServerReachable == true, "the return of the server was not noticed");
        Assert.True(alice.Offline.Status.IsDesynchronized);
        Assert.Equal(1, alice.Offline.Status.ServerChanges);                // Bob's rename
        Assert.Equal(ConnectionMode.Offline, alice.Offline.Mode);

        var report = await alice.Offline.GoOnlineAsync();
        Assert.Equal(1, report.Sent);                                       // the new row
        var conflict = Assert.Single(report.Conflicts);                    // her rename, over a version she did not see
        Assert.Equal(EntityErrorCode.Conflict, conflict.ErrorCode);
        await WaitUntilAsync(() => alice.Cache.Find(task.Id)?.Name == "Buy 2 bottles of milk",
            "Alice's cache did not show the server's version while the conflict waits");

        // 5. Alice keeps her version: written over the server's. Bob sees it.
        Assert.True((await alice.Offline.KeepMineAsync(conflict)).Successful);
        await WaitUntilAsync(() => bob.Cache.Entities.Count == 2 && bob.Cache.Find(task.Id)?.Name == "Buy oat milk",
            "Bob did not receive Alice's rows");
        Assert.Equal(["Buy oat milk", "Call the plumber"], bob.Cache.Entities.Select(c => c.Name).Order());
        Assert.Equal(["Buy oat milk", "Call the plumber"], alice.Cache.Entities.Select(c => c.Name).Order());
        lock (events)
        {
            Assert.Contains(OfflineEvent.ServerUnreachable, events);
            Assert.Contains(OfflineEvent.ServerAvailable, events);
        }
    }

    /// <summary>
    /// What the server's change hub does, without a database hook: after each accepted write, the change is published
    /// with the connection of its author.
    /// </summary>
    private async Task<TransportResult> PublishAsync(Task<TransportResult> write, ChangeType change, byte[] sent, string? connection)
    {
        var result = await write;
        if (result.Successful)
        {
            var row = _serializer.Deserialize<TestClient>(change == ChangeType.Delete || result.Data is not { Length: > 0 } ? sent : result.Data)!;
            _serverFeed.Publish(new EntityChange<TestClient>(row.Id, change, connection is null ? null : [connection]));
        }
        return result;
    }

    private sealed class Client : IAsyncDisposable
    {
        private readonly ChangeRelay<TestClient> _relay;
        private readonly TwoClientsScenarioTests _test;

        private Client(string name, TwoClientsScenarioTests test, Network network, IEntityStoreFactory remote)
        {
            _test = test;
            Network = network;
            var registry = new ChangeFeedRegistry();
            var feed = new ChangeFeed<TestClient>();
            registry.Add(feed);
            Offline = new OfflineMode(network, test._serializer, new MemoryOfflineStorage(), registry)
            {
                ProbeInterval = TimeSpan.FromMilliseconds(20),
            }.Add<TestClient, long>(c => c.Id);
            Stores = new OfflineStoreFactory(remote, Offline);
            Cache = new EntityCache<TestClient, long>(Stores, c => c.Id, feed, EntityKey.Parse<long>);
            _relay = new ChangeRelay<TestClient>(feed, () => network.Down ? null : name);
            test._serverFeed.ChangeReceived += OnServerChange;
        }

        public Network Network { get; }

        public OfflineMode Offline { get; }

        public IEntityStoreFactory Stores { get; }

        public EntityCache<TestClient, long> Cache { get; }

        public static async Task<Client> StartAsync(string name, TwoClientsScenarioTests test)
        {
            var network = new Network(test, name);
            var session = new ChangeSession();
            session.SetSession(typeof(TestClient), (name, () => Task.CompletedTask));
            var client = new Client(name, test, network, RemoteEntityStore.Factory(network, test._serializer, session));
            await client.Offline.InitializeAsync();
            await client.Offline.RefreshSnapshotAsync();
            await client.Cache.LoadAsync();
            return client;
        }

        private void OnServerChange(object? sender, EntityChange<TestClient> change)
        {
            if (!Network.Down)
                _relay.Receive(change);
        }

        public async ValueTask DisposeAsync()
        {
            _test._serverFeed.ChangeReceived -= OnServerChange;
            _relay.Dispose();
            Cache.Dispose();
            await Offline.DisposeAsync();
        }
    }

    /// <summary>The network of one client, in front of the server; it publishes the server's changes after each write.</summary>
    private sealed class Network(TwoClientsScenarioTests test, string client) : IEntityTransport
    {
        private readonly IEntityTransport _server = test._dispatcher.AsTransport();

        public volatile bool Down;

        private Task<TransportResult> Call(Func<Task<TransportResult>> call)
            => Down ? Task.FromException<TransportResult>(new HttpRequestException($"{client}: no network.")) : call();

        public Task<TransportResult> GetAllAsync(string type, CancellationToken ct = default) => Call(() => _server.GetAllAsync(type, ct));

        public Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, bool any, CancellationToken ct = default)
            => Call(() => _server.SelectAsync(type, conditions, any, ct));

        public Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
            CancellationToken ct = default)
            => Call(() => _server.SelectPagedAsync(type, conditions, any, pageNumber, pageSize, ct));

        public Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
            SortKey[] sort, CancellationToken ct = default)
            => Call(() => _server.SelectPagedAsync(type, conditions, any, pageNumber, pageSize, sort, ct));

        public Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default)
            => Call(() => _server.GetChangesAsync(type, sinceUtc, ct));

        public Task<TransportResult> InsertAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
            => Call(() => test.PublishAsync(_server.InsertAsync(type, entity, connectionId, ct), ChangeType.Insert, entity, connectionId));

        public Task<TransportResult> UpdateAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
            => Call(() => test.PublishAsync(_server.UpdateAsync(type, entity, connectionId, ct), ChangeType.Update, entity, connectionId));

        public Task<TransportResult> DeleteAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
            => Call(() => test.PublishAsync(_server.DeleteAsync(type, entity, connectionId, ct), ChangeType.Delete, entity, connectionId));

        public Task<TransportResult> ExecuteBatchAsync(BatchOperation[] operations, CancellationToken ct = default)
            => Call(() => _server.ExecuteBatchAsync(operations, ct));
    }
}

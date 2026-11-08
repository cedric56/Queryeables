using Queryeables.Abstraction;
using Queryeables.Remote;
using Queryeables.Remote.Offline;

namespace Queryeables.Tests.Load;

/// <summary>
/// Several clients of one server, writing at the same time, online and offline, while their network comes and goes and
/// answers get lost on the way back. Whatever the order of events, once every network is back and every client has
/// synchronized, they must all agree with the server, and no write may have been applied twice.
/// <para>
/// The events are drawn from a seed: a failure prints it, and <c>Run(seed)</c> replays the same choices (the threads still
/// interleave as they do). <c>QUERYEABLES_LOAD_SCALE</c> multiplies the clients and the steps, for a longer run.
/// </para>
/// </summary>
public class ConvergenceTests
{
    private static int Scale => int.TryParse(Environment.GetEnvironmentVariable("QUERYEABLES_LOAD_SCALE"), out var scale) && scale > 0 ? scale : 1;

    [Fact]
    public Task Clients_converge_with_the_server_seed_1() => new Run(seed: 1, clients: 4 * Scale, steps: 60 * Scale).ExecuteAsync();

    [Fact]
    public Task Clients_converge_with_the_server_seed_2() => new Run(seed: 2, clients: 4 * Scale, steps: 60 * Scale).ExecuteAsync();

    [Fact]
    public Task Clients_converge_with_the_server_seed_3() => new Run(seed: 3, clients: 6 * Scale, steps: 40 * Scale).ExecuteAsync();

    /// <summary>One run: a server, its clients, the random events, and the checks at the end.</summary>
    private sealed class Run(int seed, int clients, int steps)
    {
        private readonly MemoryDatabase _database = new();
        private readonly JsonTestSerializer _serializer = new();
        private readonly ChangeFeed<TestClient> _serverFeed = new();

        // The server handles one call at a time, as a serializable database would: MemoryDatabase is not thread-safe.
        private readonly SemaphoreSlim _serverLock = new(1, 1);
        private EntityRequestDispatcher _dispatcher = null!;

        public async Task ExecuteAsync()
        {
            var stores = new EntityStoreFactory(() => new MemoryStore(_database).WithSyncConventions());
            var registry = new EntityHandlerRegistry();
            registry.Register(new EntityHandler<TestClient>(stores, EntityAccessPolicies.AllowAll));
            _dispatcher = new EntityRequestDispatcher(registry, _serializer, stores);

            await using (var seedStore = stores.Create())
            {
                for (var i = 0; i < 5; i++)
                    await seedStore.InsertAsync(new TestClient { Name = $"s:{i}" });
            }

            var all = new List<Client>();
            try
            {
                for (var i = 0; i < clients; i++)
                    all.Add(await Client.StartAsync($"c{i}", this, new Random(HashCode.Combine(seed, i))));

                // 1. Every client works at the same time.
                await Task.WhenAll(all.Select(c => Task.Run(() => c.WorkAsync(steps))));

                // 2. Every network is back, nothing is lost anymore: each client sends what it kept, the server wins the
                //    conflicts left.
                foreach (var client in all)
                    await client.SettleAsync();

                // 3. Every client agrees with the server, and no write was applied twice.
                await CheckAsync(all);

                // 4. The clients follow the server live again: a change made by one reaches all the others.
                await all[0].InsertOnlineAsync("live");
                foreach (var client in all.Skip(1))
                    await WaitUntilAsync(() => client.Cache.Entities.Any(c => c.Name == "live"), $"{client.Name} did not receive the live change");
            }
            catch (Exception ex)
            {
                Assert.Fail($"Convergence failed with seed {seed} ({clients} clients, {steps} steps): {ex}");
            }
            finally
            {
                foreach (var client in all)
                    await client.DisposeAsync();
            }
        }

        private async Task CheckAsync(List<Client> all)
        {
            var server = _database.All<TestClient>();
            var live = server.Where(c => !c.Deleted).OrderBy(c => c.Id).Select(c => (c.Id, c.Name, c.Version)).ToList();

            // Every write gives a name never given before ("c2:17", renamed "c2:17/c3r4"), and a row is never written twice
            // with the same name: two rows with one name (deleted ones included) are one write applied twice. Keeping an
            // offline rename over a row deleted meanwhile inserts it again, under that new name: still one row per name.
            var duplicated = server.GroupBy(c => c.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(duplicated.Count == 0, $"Applied twice: {string.Join(", ", duplicated)}");

            foreach (var client in all)
            {
                Assert.Equal(0, client.Offline.Status.PendingChanges);
                Assert.Empty(client.Offline.Conflicts);
                Assert.Equal(ConnectionMode.Online, client.Offline.Mode);

                await client.Cache.LoadAsync();
                var cached = client.Cache.Entities.OrderBy(c => c.Id).Select(c => (c.Id, c.Name, c.Version)).ToList();
                Assert.True(live.SequenceEqual(cached),
                    $"{client.Name} differs from the server: missing {string.Join(", ", live.Except(cached))}; extra {string.Join(", ", cached.Except(live))}");

                // Its offline copy too, once taken again.
                Assert.True((await client.Offline.RefreshSnapshotAsync()).Successful);
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition, string because)
        {
            for (var i = 0; i < 500 && !condition(); i++)
                await Task.Delay(10);
            Assert.True(condition(), because);
        }

        /// <summary>After each accepted write, the server's change hub publishes the row, with its author's connection.</summary>
        private async Task<TransportResult> PublishAsync(Task<TransportResult> write, BatchOperationKind kind, byte[] sent, string? connection)
        {
            var result = await write;
            if (result.Successful)
            {
                var data = kind == BatchOperationKind.Delete || result.Data is not { Length: > 0 } ? sent : result.Data;
                var row = _serializer.Deserialize<TestClient>(data)!;
                var change = kind switch
                {
                    BatchOperationKind.Insert => ChangeType.Insert,
                    BatchOperationKind.Update => ChangeType.Update,
                    _ => ChangeType.Delete,
                };
                _serverFeed.Publish(new EntityChange<TestClient>(row.Id, change, connection is null ? null : [connection]));
            }
            return result;
        }

        private sealed class Client : IAsyncDisposable
        {
            private readonly Run _run;
            private readonly Random _random;
            private readonly ChangeRelay<TestClient> _relay;
            private int _inserted;
            private int _renamed;

            private Client(string name, Run run, Random random, Network network, IEntityStoreFactory remote)
            {
                Name = name;
                _run = run;
                _random = random;
                Network = network;
                var registry = new ChangeFeedRegistry();
                var feed = new ChangeFeed<TestClient>();
                registry.Add(feed);
                Offline = new OfflineMode(network, run._serializer, new MemoryOfflineStorage(), registry)
                {
                    ProbeInterval = TimeSpan.FromMilliseconds(20),
                    ProbeTimeout = TimeSpan.FromSeconds(2),
                }.Add<TestClient, long>(c => c.Id);
                Stores = new OfflineStoreFactory(remote, Offline);
                Cache = new EntityCache<TestClient, long>(Stores, c => c.Id, feed, EntityKey.Parse<long>);
                _relay = new ChangeRelay<TestClient>(feed, () => network.Down ? null : name);
                run._serverFeed.ChangeReceived += OnServerChange;
            }

            public string Name { get; }

            public Network Network { get; }

            public OfflineMode Offline { get; }

            public IEntityStoreFactory Stores { get; }

            public EntityCache<TestClient, long> Cache { get; }

            public static async Task<Client> StartAsync(string name, Run run, Random random)
            {
                var network = new Network(run, name, random);
                var session = new ChangeSession();
                session.SetSession(typeof(TestClient), (name, () => Task.CompletedTask));
                var client = new Client(name, run, random, network, RemoteEntityStore.Factory(network, run._serializer, session));
                await client.Offline.InitializeAsync();
                await client.Offline.RefreshSnapshotAsync();
                await client.Cache.LoadAsync();
                return client;
            }

            /// <summary>What a user does: reads, writes, loses the network, works offline, synchronizes, decides conflicts.</summary>
            public async Task WorkAsync(int steps)
            {
                for (var step = 0; step < steps; step++)
                {
                    lock (_random)
                    {
                        if (_random.NextDouble() < 0.1)
                            Network.Down = !Network.Down;
                        Network.LoseAnswers = _random.NextDouble() < 0.3 ? 0.15 : 0;
                    }

                    try
                    {
                        if (Offline.Mode == ConnectionMode.Offline && !Network.Down && Chance(0.2))
                            await SynchronizeAsync();
                        else if (Offline.Conflicts.Count > 0 && !Network.Down && Chance(0.3))
                            await DecideAsync();
                        else
                            await WriteAsync();
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex.InnerException is HttpRequestException)
                    {
                        // Online without network: the user is told, and chooses to work offline.
                        if (Offline.Mode == ConnectionMode.Online)
                            await Offline.GoOfflineAsync();
                    }
                    catch (EntityStoreException ex) when (ex.ErrorCode is EntityErrorCode.Conflict or EntityErrorCode.NotFound)
                    {
                        // Someone else changed or deleted the row: read again at the next step.
                    }

                    if (Chance(0.3))
                        await Task.Yield();
                }
            }

            private bool Chance(double probability)
            {
                lock (_random)
                    return _random.NextDouble() < probability;
            }

            private int Next(int max)
            {
                lock (_random)
                    return _random.Next(max);
            }

            private async Task WriteAsync()
            {
                await using var store = Stores.Create();
                var rows = await store.GetAllAsync<TestClient>();
                var choice = Next(10);
                if (choice < 4 || rows.Count == 0)
                {
                    await store.InsertAsync(new TestClient { Name = $"{Name}:{_inserted++}" });
                }
                else if (choice < 8)
                {
                    var row = rows[Next(rows.Count)];
                    row.Name = $"{row.Name.Split('/')[0]}/{Name}r{_renamed++}";
                    await store.UpdateAsync(row);
                }
                else
                {
                    await store.DeleteAsync(rows[Next(rows.Count)]);
                }
            }

            private async Task SynchronizeAsync()
            {
                await Offline.GoOnlineAsync();
                await DecideAsync();
            }

            /// <summary>
            /// The user decides the conflicts waiting, at any time: keep the offline write (sent again over the server's
            /// row), or the server's. A decision whose answer was lost leaves its conflict, decided again later.
            /// </summary>
            private async Task DecideAsync()
            {
                foreach (var conflict in Offline.Conflicts.ToList())
                {
                    if (Chance(0.6))
                        await Offline.KeepMineAsync(conflict);
                    else
                        await Offline.KeepServerAsync(conflict);
                }
            }

            /// <summary>The network is back for good: send what was kept, let the server win what is still in conflict.</summary>
            public async Task SettleAsync()
            {
                Network.Down = false;
                Network.LoseAnswers = 0;
                for (var attempt = 0; attempt < 10 && (Offline.Mode == ConnectionMode.Offline || Offline.Status.PendingChanges > 0); attempt++)
                    await Offline.GoOnlineAsync();
                foreach (var conflict in Offline.Conflicts.ToList())
                    await Offline.KeepServerAsync(conflict);
            }

            public async Task InsertOnlineAsync(string name)
            {
                await using var store = Stores.Create();
                await store.InsertAsync(new TestClient { Name = name });
            }

            private void OnServerChange(object? sender, EntityChange<TestClient> change)
            {
                if (!Network.Down)
                    _relay.Receive(change);
            }

            public async ValueTask DisposeAsync()
            {
                _run._serverFeed.ChangeReceived -= OnServerChange;
                _relay.Dispose();
                Cache.Dispose();
                await Offline.DisposeAsync();
            }
        }

        /// <summary>
        /// The network of one client: down when <see cref="Down"/>, and losing a share of the answers to writes (the write
        /// is applied, the client is told the connection dropped).
        /// </summary>
        private sealed class Network(Run run, string client, Random random) : IEntityTransport
        {
            private readonly IEntityTransport _server = run._dispatcher.AsTransport();

            public volatile bool Down;

            public double LoseAnswers;

            private async Task<TransportResult> Call(Func<Task<TransportResult>> call, bool write = false)
            {
                if (Down)
                    throw new HttpRequestException($"{client}: no network.");

                TransportResult result;
                await run._serverLock.WaitAsync();
                try
                {
                    result = await call();
                }
                finally
                {
                    run._serverLock.Release();
                }

                bool lost;
                lock (random)
                    lost = write && random.NextDouble() < LoseAnswers;
                return lost ? throw new HttpRequestException($"{client}: the connection was reset.") : result;
            }

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
                => Call(() => run.PublishAsync(_server.InsertAsync(type, entity, connectionId, ct), BatchOperationKind.Insert, entity, connectionId), write: true);

            public Task<TransportResult> UpdateAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
                => Call(() => run.PublishAsync(_server.UpdateAsync(type, entity, connectionId, ct), BatchOperationKind.Update, entity, connectionId), write: true);

            public Task<TransportResult> DeleteAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
                => Call(() => run.PublishAsync(_server.DeleteAsync(type, entity, connectionId, ct), BatchOperationKind.Delete, entity, connectionId), write: true);

            public Task<TransportResult> ExecuteBatchAsync(BatchOperation[] operations, CancellationToken ct = default)
                => Call(() => _server.ExecuteBatchAsync(operations, ct), write: true);

            public Task<TransportResult> WriteOnceAsync(Guid operationId, BatchOperationKind kind, string type, byte[] entity, string? connectionId,
                CancellationToken ct = default)
                => Call(() => run.PublishAsync(_server.WriteOnceAsync(operationId, kind, type, entity, connectionId, ct), kind, entity, connectionId),
                    write: true);
        }
    }
}

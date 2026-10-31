using Microsoft.Extensions.DependencyInjection;
using Queryeables.Abstraction;
using Queryeables.Remote;
using Queryeables.Remote.Offline;

namespace Queryeables.Tests.Unit;

/// <summary>
/// The offline mode against a server whose network can be cut: writes kept and queued offline, the user told when the
/// server is back, and nothing switched without the user.
/// </summary>
public class OfflineModeTests
{
    private readonly MemoryDatabase _server = new();
    private readonly NetworkRemote _network;
    private readonly MemoryOfflineStorage _storage = new();
    private readonly JsonTestSerializer _serializer = new();

    public OfflineModeTests()
    {
        // [LastModified] and [SoftDelete] maintained by the server, as the offline mode expects.
        var serverStores = new EntityStoreFactory(() => new MemoryStore(_server).WithSyncConventions());
        var graph = PersonGraph();
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<TestClient>(serverStores, EntityAccessPolicies.AllowAll));
        registry.Register(new EntityHandler<AuditPerson>(serverStores, EntityAccessPolicies.AllowAll,
            aggregate: new EntityGraphService<AuditPerson, long>(serverStores, graph)));
        registry.Register(new EntityHandler<AuditContact>(serverStores, EntityAccessPolicies.AllowAll));
        registry.Register(new EntityHandler<GuidNote>(serverStores, EntityAccessPolicies.AllowAll));
        _network = new NetworkRemote(new EntityRequestDispatcher(registry, _serializer, serverStores).AsRemote());
    }

    private static EntityGraph<AuditPerson, long> PersonGraph()
        => EntityGraph.For<AuditPerson, long>(p => p.Id).HasMany(p => p.Contacts, foreignKey: c => c.PersonId, key: c => c.Id);

    /// <summary>The client: the offline mode, its store factory and a person service sending parents with their children.</summary>
    private (OfflineMode Mode, IEntityStoreFactory Stores, IDbTransaction<AuditPerson> Persons) Client(TimeSpan? probe = null,
        System.Linq.Expressions.Expression<Func<TestClient, bool>>? clients = null, IOfflineStorage? storage = null,
        DbMonitorRegistry? registry = null)
    {
        var services = new Services();
        var mode = new OfflineMode(_network, _serializer, storage ?? _storage, registry, services)
        {
            ProbeInterval = probe ?? TimeSpan.FromHours(1),
            ProbeTimeout = TimeSpan.FromSeconds(2),
        }
            .Add<TestClient, long>(c => c.Id, clients)
            .Add<AuditPerson, long>(p => p.Id)
            .Add<AuditContact, long>(c => c.Id)
            .Add<GuidNote, Guid>(n => n.Id);
        var stores = new OfflineStoreFactory(RemoteEntityStore.Factory(_network, _serializer), mode);
        var persons = new EntityGraphService<AuditPerson, long>(stores, PersonGraph());
        services.Add(typeof(IDbTransaction<AuditPerson>), persons);
        return (mode, stores, persons);
    }

    private void Seed(params string[] names)
    {
        var store = new MemoryStore(_server).WithSyncConventions();
        foreach (var name in names)
            store.InsertAsync(new TestClient { Name = name }).GetAwaiter().GetResult();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), because);
    }

    [Fact]
    public async Task Offline_the_stores_read_the_copy_and_queue_the_writes()
    {
        Seed("Ana", "Bob");
        var (mode, stores, _) = Client();
        Assert.True((await mode.GoOfflineAsync()).Successful);
        _network.Down = true;

        await using var store = stores.Create();
        var carl = new TestClient { Name = "Carl" };
        await store.InsertAsync(carl);
        var bob = (await store.SelectAsync<TestClient>(c => c.Name == "Bob")).Single();
        bob.Name = "Bobby";
        Assert.True(await store.UpdateAsync(bob));

        Assert.True(carl.Id < 0, "a row created offline gets a temporary key");
        Assert.Equal(["Ana", "Bobby", "Carl"], (await store.GetAllAsync<TestClient>()).Select(c => c.Name).Order());
        Assert.Equal(2, mode.Status.PendingChanges);
        Assert.Equal(["Ana", "Bob"], _server.All<TestClient>().Select(c => c.Name).Order());
    }

    [Fact]
    public async Task Writes_on_one_row_are_coalesced()
    {
        var (mode, stores, _) = Client();
        await mode.GoOfflineAsync();
        await using var store = stores.Create();

        var kept = new TestClient { Name = "Kept" };
        await store.InsertAsync(kept);
        kept.Name = "Kept twice";
        await store.UpdateAsync(kept);
        var dropped = new TestClient { Name = "Dropped" };
        await store.InsertAsync(dropped);
        await store.DeleteAsync(dropped);

        Assert.Equal(1, mode.Status.PendingChanges);     // one insert, with the last name; nothing for the other row
        Assert.True((await mode.GoOnlineAsync()).Completed);
        Assert.Equal(["Kept twice"], _server.All<TestClient>().Select(c => c.Name));
    }

    [Fact]
    public async Task Going_offline_needs_a_copy_when_the_server_cannot_be_reached()
    {
        var (mode, _, _) = Client();
        _network.Down = true;

        var result = await mode.GoOfflineAsync();

        Assert.False(result.Successful);
        Assert.Equal(ConnectionMode.Online, mode.Mode);
    }

    [Fact]
    public async Task The_copy_the_queue_and_the_mode_survive_a_restart()
    {
        Seed("Ana");
        var (first, stores, _) = Client();
        await first.GoOfflineAsync();
        await using (var store = stores.Create())
            await store.InsertAsync(new TestClient { Name = "Offline" });
        await first.DisposeAsync();

        _network.Down = true;
        var (second, secondStores, _) = Client();
        await second.InitializeAsync();

        Assert.Equal(ConnectionMode.Offline, second.Mode);
        Assert.Equal(1, second.Status.PendingChanges);
        await using var reopened = secondStores.Create();
        Assert.Equal(["Ana", "Offline"], (await reopened.GetAllAsync<TestClient>()).Select(c => c.Name).Order());
    }

    [Fact]
    public async Task A_lost_server_is_reported_and_its_return_too_without_switching()
    {
        Seed("Ana");
        var (mode, stores, _) = Client(probe: TimeSpan.FromMilliseconds(20));
        await mode.InitializeAsync();
        var events = new List<OfflineEvent>();
        mode.StatusChanged += (_, e) => { lock (events) events.Add(e.Reason); };

        _network.Down = true;
        await using (var store = stores.Create())
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => store.GetAllAsync<TestClient>());
        Assert.Equal(false, mode.Status.ServerReachable);
        Assert.Equal(ConnectionMode.Online, mode.Mode);     // told, not switched

        _network.Down = false;
        await WaitUntilAsync(() => mode.Status.ServerReachable == true, "the return of the server was not noticed");

        lock (events)
            Assert.Equal([OfflineEvent.ServerUnreachable, OfflineEvent.ServerAvailable], events);
        Assert.Equal(ConnectionMode.Online, mode.Mode);
        await mode.DisposeAsync();
    }

    [Fact]
    public async Task Offline_the_return_of_the_server_says_whether_the_data_differs()
    {
        Seed("Ana");
        var (mode, stores, _) = Client(probe: TimeSpan.FromMilliseconds(20));
        await mode.GoOfflineAsync();
        _network.Down = true;
        await WaitUntilAsync(() => mode.Status.ServerReachable == false, "the loss was not noticed");
        await using (var store = stores.Create())
            await store.InsertAsync(new TestClient { Name = "Offline" });

        // Meanwhile, someone else changes a row on the server.
        _server.All<TestClient>().Single().LastModified = DateTime.UtcNow.AddMinutes(1);
        OfflineStatus? available = null;
        mode.StatusChanged += (_, e) => { if (e.Reason == OfflineEvent.ServerAvailable) available = e.Status; };
        _network.Down = false;
        await WaitUntilAsync(() => available is not null, "the return of the server was not reported");

        Assert.True(available!.IsDesynchronized);
        Assert.Equal(1, available.PendingChanges);
        Assert.Equal(1, available.ServerChanges);
        Assert.Equal(ConnectionMode.Offline, mode.Mode);     // the user decides
        await mode.DisposeAsync();
    }

    [Fact]
    public async Task Synchronizing_sends_the_writes_and_takes_the_server_copy()
    {
        Seed("Ana");
        var (mode, stores, _) = Client();
        await mode.GoOfflineAsync();
        _network.Down = true;
        await using (var store = stores.Create())
        {
            await store.InsertAsync(new TestClient { Name = "Offline" });
            var ana = (await store.GetAllAsync<TestClient>()).Single(c => c.Name == "Ana");
            await store.DeleteAsync(ana);
        }

        var refused = await mode.GoOnlineAsync();
        Assert.False(refused.Completed);
        Assert.Equal(ConnectionMode.Offline, mode.Mode);

        _network.Down = false;
        var report = await mode.GoOnlineAsync();

        Assert.True(report.Completed);
        Assert.Equal(2, report.Sent);
        Assert.Empty(report.Conflicts);
        Assert.Equal(ConnectionMode.Online, mode.Mode);
        Assert.Equal(0, mode.Status.PendingChanges);
        var row = Assert.Single(_server.All<TestClient>().Where(c => !c.Deleted));     // Ana: soft-deleted
        Assert.Equal("Offline", row.Name);
        Assert.True(row.Id > 0, "the server gave its own key");
    }

    [Fact]
    public async Task A_refused_write_waits_for_the_user()
    {
        Seed("Ana", "Bob");
        var (mode, stores, _) = Client();
        await mode.GoOfflineAsync();
        await using (var store = stores.Create())
        {
            foreach (var client in await store.GetAllAsync<TestClient>())
            {
                client.Name += " (offline)";
                await store.UpdateAsync(client);
            }
        }

        // Deleted on the server meanwhile.
        var server = new MemoryStore(_server);
        foreach (var client in _server.All<TestClient>())
            await server.DeleteAsync(client);

        var report = await mode.GoOnlineAsync();

        Assert.True(report.Completed);
        Assert.Equal(2, report.Conflicts.Count);
        Assert.All(report.Conflicts, c => Assert.Equal(EntityErrorCode.NotFound, c.ErrorCode));
        Assert.Equal(2, mode.Status.Conflicts);
        Assert.True(mode.Status.IsDesynchronized);

        var mine = report.Conflicts.Single(c => mode.ReadConflict<TestClient>(c)!.Name == "Ana (offline)");
        var theirs = report.Conflicts.Single(c => c != mine);
        Assert.True((await mode.KeepMineAsync(mine)).Successful);
        await mode.KeepServerAsync(theirs);

        Assert.Equal(["Ana (offline)"], _server.All<TestClient>().Select(c => c.Name));
        Assert.Empty(mode.Conflicts);
    }

    [Fact]
    public async Task A_parent_and_its_children_are_saved_together_offline_then_on_the_server()
    {
        var (mode, stores, persons) = Client();
        await mode.GoOfflineAsync();
        _network.Down = true;

        var ana = new AuditPerson { Name = "Ana", Contacts = [new() { Label = "mail" }, new() { Label = "phone" }] };
        Assert.True((await persons.SaveAsync(ana)).Successful);

        Assert.True(ana.Id < 0);
        Assert.All(ana.Contacts, c => Assert.True(c.Id < 0 && c.PersonId == ana.Id));
        await using (var store = stores.Create())
            Assert.Equal(2, (await store.SelectAsync<AuditContact>(c => c.PersonId == ana.Id)).Count);
        Assert.Equal(1, mode.Status.PendingChanges);     // the parent, its children inside

        _network.Down = false;
        var report = await mode.GoOnlineAsync();

        Assert.True(report.Completed);
        var saved = Assert.Single(_server.All<AuditPerson>());
        Assert.True(saved.Id > 0);
        Assert.Equal(["mail", "phone"], _server.All<AuditContact>().Where(c => c.PersonId == saved.Id).Select(c => c.Label).Order());
    }

    [Fact]
    public async Task An_offline_transaction_not_committed_leaves_nothing()
    {
        Seed("Ana");
        var (mode, stores, _) = Client();
        await mode.GoOfflineAsync();

        await using var store = stores.Create();
        var result = await store.RunInTransactionAsync(async s =>
        {
            await s.InsertAsync(new TestClient { Name = "Carl" });
            throw new InvalidOperationException("stop");
        });

        Assert.False(result.Successful);
        Assert.Equal(["Ana"], (await store.GetAllAsync<TestClient>()).Select(c => c.Name));
        Assert.Equal(0, mode.Status.PendingChanges);
    }

    [Fact]
    public async Task A_type_not_made_available_offline_is_refused_offline()
    {
        var (mode, stores, _) = Client();
        await mode.GoOfflineAsync();
        await using var store = stores.Create();

        await Assert.ThrowsAsync<NotSupportedException>(() => store.GetAllAsync<AuditNote>());
    }

    [Fact]
    public async Task WithOfflineMode_puts_the_offline_stores_behind_the_registered_factory()
    {
        Seed("Ana");
        var services = new ServiceCollection();
        services.AddSingleton<IEntityRemote>(_network);
        services.AddSingleton<IEntitySerializer>(_serializer);
        services.UseQueryeables(q => q
            .UseStores(sp => RemoteEntityStore.Factory(sp.GetRequiredService<IEntityRemote>(), sp.GetRequiredService<IEntitySerializer>()))
            .WithOfflineMode(o => o.UseStorage(_storage).Entity<TestClient, long>(c => c.Id)));
        await using var provider = services.BuildServiceProvider();

        var mode = provider.GetRequiredService<OfflineMode>();
        await mode.InitializeAsync();
        await mode.GoOfflineAsync();
        _network.Down = true;

        var stores = provider.GetRequiredService<IEntityStoreFactory>();
        Assert.True(stores.IsRemote);
        await using var store = stores.Create();
        Assert.Equal(["Ana"], (await store.GetAllAsync<TestClient>()).Select(c => c.Name));
    }

    [Fact]
    public async Task File_storage_writes_reads_and_deletes()
    {
        var folder = Path.Combine(Path.GetTempPath(), "queryeables-offline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new FileOfflineStorage(folder);
            await storage.WriteAsync("app/Person/rows", [1, 2, 3]);
            await storage.WriteAsync("app/Person/rows", [4, 5]);

            Assert.Equal(new byte[] { 4, 5 }, await storage.ReadAsync("app/Person/rows"));
            await storage.DeleteAsync("app/Person/rows");
            Assert.Null(await storage.ReadAsync("app/Person/rows"));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_write_saves_its_own_row_only()
    {
        Seed([.. Enumerable.Range(1, 50).Select(i => $"Client {i}")]);
        var storage = new CountingStorage();
        var (mode, stores, _) = Client(storage: storage);
        await mode.GoOfflineAsync();
        storage.Written.Clear();

        await using var store = stores.Create();
        var one = (await store.SelectAsync<TestClient>(c => c.Name == "Client 7")).Single();
        one.Name = "Seven";
        await store.UpdateAsync(one);

        Assert.Equal([$"queryeables-offline/{EntityName.Of<TestClient>()}/row/{one.Id}"], storage.Written.Where(k => k.Contains("/row/")));
        Assert.DoesNotContain($"queryeables-offline/{EntityName.Of<TestClient>()}/index", storage.Written);   // same rows: no new list
        Assert.Single(storage.Written, k => k.StartsWith("queryeables-offline/outbox/", StringComparison.Ordinal) && !k.EndsWith("index", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_copy_only_downloads_what_changed()
    {
        Seed("Ana", "Bob", "Carl");
        var (mode, stores, _) = Client();
        await mode.RefreshSnapshotAsync();
        Assert.Equal(1, _network.FullReads);

        // On the server: one row changed, one added, one deleted (soft delete, as with the sync conventions).
        var server = new MemoryStore(_server).WithSyncConventions();
        var rows = await server.GetAllAsync<TestClient>();
        var bob = rows.Single(c => c.Name == "Bob");
        bob.Name = "Bobby";
        await server.UpdateAsync(bob);
        await server.DeleteAsync(rows.Single(c => c.Name == "Carl"));
        await server.InsertAsync(new TestClient { Name = "Dana" });

        Assert.True((await mode.RefreshSnapshotAsync()).Successful);

        Assert.Equal(1, _network.FullReads);     // no second full download
        await mode.GoOfflineAsync();
        await using var store = stores.Create();
        Assert.Equal(["Ana", "Bobby", "Dana"], (await store.GetAllAsync<TestClient>()).Select(c => c.Name).Order());
    }

    [Fact]
    public async Task Rows_written_offline_are_read_again_from_the_server()
    {
        Seed("Ana", "Bob");
        var (mode, stores, _) = Client();
        await mode.GoOfflineAsync();
        await using (var store = stores.Create())
        {
            var ana = (await store.GetAllAsync<TestClient>()).Single(c => c.Name == "Ana");
            ana.Name = "";      // refused by the server: [Required]
            await store.UpdateAsync(ana);
            await store.InsertAsync(new TestClient { Name = "Carl" });
        }

        var report = await mode.GoOnlineAsync();
        await mode.GoOfflineAsync();

        Assert.Single(report.Conflicts);
        Assert.Equal(1, _network.FullReads);     // the first copy only
        await using var reopened = stores.Create();
        var names = (await reopened.GetAllAsync<TestClient>()).ToList();
        Assert.Equal(["Ana", "Bob", "Carl"], names.Select(c => c.Name).Order());     // the server's Ana, not the refused one
        Assert.All(names, c => Assert.True(c.Id > 0));                                 // no temporary key left
    }

    [Fact]
    public async Task Only_the_rows_of_the_scope_are_copied_and_a_new_scope_copies_again()
    {
        var server = new MemoryStore(_server).WithSyncConventions();
        await server.InsertAsync(new TestClient { Name = "Ana", SponsorId = 1 });
        await server.InsertAsync(new TestClient { Name = "Bob", SponsorId = 2 });
        await server.InsertAsync(new TestClient { Name = "Carl", SponsorId = 1 });
        var sponsor = 1;
        var (mode, stores, _) = Client(clients: c => c.SponsorId == sponsor);

        await mode.GoOfflineAsync();
        await using (var store = stores.Create())
            Assert.Equal(["Ana", "Carl"], (await store.GetAllAsync<TestClient>()).Select(c => c.Name).Order());

        await mode.GoOnlineAsync();
        sponsor = 2;                       // another user
        await mode.GoOfflineAsync();
        await using (var store = stores.Create())
            Assert.Equal(["Bob"], (await store.GetAllAsync<TestClient>()).Select(c => c.Name));
    }

    [Fact]
    public async Task A_connection_lost_during_a_synchronization_keeps_what_was_not_sent()
    {
        var (mode, stores, _) = Client();
        await mode.GoOfflineAsync();
        await using (var store = stores.Create())
        {
            foreach (var name in new[] { "One", "Two", "Three" })
                await store.InsertAsync(new TestClient { Name = name });
        }

        _network.DownAfterWrites = 1;
        var cut = await mode.GoOnlineAsync();

        Assert.False(cut.Completed);
        Assert.Equal(1, cut.Sent);
        Assert.Equal(2, mode.Status.PendingChanges);
        Assert.Equal(ConnectionMode.Offline, mode.Mode);
        Assert.Equal(["One"], _server.All<TestClient>().Select(c => c.Name));

        _network.Down = false;
        var report = await mode.GoOnlineAsync();

        Assert.True(report.Completed);
        Assert.Equal(2, report.Sent);
        Assert.Equal(["One", "Three", "Two"], _server.All<TestClient>().Select(c => c.Name).Order());     // nothing sent twice
    }

    [Fact]
    public async Task Conflicts_survive_a_restart()
    {
        Seed("Ana");
        var (first, stores, _) = Client();
        await first.GoOfflineAsync();
        await using (var store = stores.Create())
        {
            var ana = (await store.GetAllAsync<TestClient>()).Single();
            ana.Name = "";      // refused: [Required]
            await store.UpdateAsync(ana);
        }
        var report = await first.GoOnlineAsync();
        await first.DisposeAsync();

        var (second, _, _) = Client();
        await second.InitializeAsync();

        var conflict = Assert.Single(second.Conflicts);
        Assert.Equal(report.Conflicts[0].Id, conflict.Id);
        Assert.Equal(EntityErrorCode.Validation, conflict.ErrorCode);
        Assert.Equal("", second.ReadConflict<TestClient>(conflict)!.Name);
        await second.KeepServerAsync(conflict);
        Assert.Empty(second.Conflicts);
    }

    [Fact]
    public async Task A_guid_key_created_offline_is_a_new_guid()
    {
        var (mode, stores, _) = Client();
        await mode.GoOfflineAsync();
        await using var store = stores.Create();

        var note = new GuidNote { Text = "offline" };
        await store.InsertAsync(note);

        Assert.NotEqual(Guid.Empty, note.Id);
        Assert.Equal(note.Id, (await store.GetAllAsync<GuidNote>()).Single().Id);
    }

    [Fact]
    public async Task Offline_writes_and_switches_notify_the_monitors()
    {
        Seed("Ana");
        var registry = new DbMonitorRegistry();
        var monitor = new DbMonitor<TestClient>();
        registry.Add(monitor);
        var received = new List<DbRecord<TestClient>>();
        monitor.RecordChanged += (_, record) => received.Add(record);
        var (mode, stores, _) = Client(registry: registry);

        await mode.GoOfflineAsync();
        await using (var store = stores.Create())
        {
            var carl = new TestClient { Name = "Carl" };
            await store.InsertAsync(carl);
            carl.Name = "Carla";
            await store.UpdateAsync(carl);
            await store.DeleteAsync(carl);
        }
        await mode.GoOnlineAsync();

        Assert.Equal(
            ["Update:", "Insert:-1", "Update:-1", "Delete:-1", "Update:"],      // resync, three writes, resync
            received.Select(r => $"{r.ChangeType}:{r.EntityKey}"));
    }

    [Fact]
    public async Task A_new_copy_asks_again_over_the_overlap_window()
    {
        Seed("Ana");
        var (mode, stores, _) = Client();
        await mode.RefreshSnapshotAsync();

        // Committed late: stamped a moment before the latest date the copy received.
        var latest = _server.All<TestClient>().Max(c => c.LastModified);
        new MemoryStore(_server).InsertAsync(new TestClient { Name = "Late", LastModified = latest.AddSeconds(-2) }).GetAwaiter().GetResult();

        await mode.RefreshSnapshotAsync();
        await mode.GoOfflineAsync();

        await using var store = stores.Create();
        Assert.Equal(["Ana", "Late"], (await store.GetAllAsync<TestClient>()).Select(c => c.Name).Order());
    }

    /// <summary>Remembers the keys written.</summary>
    private sealed class CountingStorage : IOfflineStorage
    {
        private readonly MemoryOfflineStorage _inner = new();

        public List<string> Written { get; } = [];

        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken ct = default) => _inner.ReadAsync(key, ct);

        public ValueTask WriteAsync(string key, byte[] value, CancellationToken ct = default)
        {
            Written.Add(key);
            return _inner.WriteAsync(key, value, ct);
        }

        public ValueTask DeleteAsync(string key, CancellationToken ct = default) => _inner.DeleteAsync(key, ct);
    }

    /// <summary>A transport whose network can be cut: every call then fails like a lost connection.</summary>
    private sealed class NetworkRemote(IEntityRemote server) : IEntityRemote
    {
        public volatile bool Down;

        /// <summary>The network goes down after this many writes.</summary>
        public int? DownAfterWrites;

        private Task<RemoteResult> Call(Func<Task<RemoteResult>> call)
            => Down ? Task.FromException<RemoteResult>(new HttpRequestException("No network.")) : call();

        private Task<RemoteResult> Write(Func<Task<RemoteResult>> call)
        {
            if (DownAfterWrites is { } left)
            {
                if (left <= 0)
                {
                    Down = true;
                    DownAfterWrites = null;
                }
                else
                {
                    DownAfterWrites = left - 1;
                }
            }
            return Call(call);
        }

        /// <summary>Full downloads of TestClient: every row, or every row of a scope.</summary>
        public int FullReads;

        public Task<RemoteResult> GetAllAsync(string type, CancellationToken ct = default)
        {
            if (type == EntityName.Of<TestClient>())
                FullReads++;
            return Call(() => server.GetAllAsync(type, ct));
        }

        public Task<RemoteResult> SelectAsync(string type, FilterCondition[] conditions, bool any, CancellationToken ct = default)
            => Call(() => server.SelectAsync(type, conditions, any, ct));

        public Task<RemoteResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
            CancellationToken ct = default)
            => Call(() => server.SelectPagedAsync(type, conditions, any, pageNumber, pageSize, ct));

        public Task<RemoteResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
            SortKey[] sort, CancellationToken ct = default)
            => Call(() => server.SelectPagedAsync(type, conditions, any, pageNumber, pageSize, sort, ct));

        public Task<RemoteResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default)
            => Call(() => server.GetChangesAsync(type, sinceUtc, ct));

        public Task<RemoteResult> InsertAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
            => Write(() => server.InsertAsync(type, entity, connectionId, ct));

        public Task<RemoteResult> UpdateAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
            => Write(() => server.UpdateAsync(type, entity, connectionId, ct));

        public Task<RemoteResult> DeleteAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
            => Write(() => server.DeleteAsync(type, entity, connectionId, ct));

        public Task<RemoteResult> ExecuteBatchAsync(BatchOperation[] operations, CancellationToken ct = default)
            => Call(() => server.ExecuteBatchAsync(operations, ct));
    }

    public sealed class GuidNote
    {
        public Guid Id { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    private sealed class Services : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = [];

        public void Add(Type type, object service) => _services[type] = service;

        public object? GetService(Type serviceType) => _services.GetValueOrDefault(serviceType);
    }
}

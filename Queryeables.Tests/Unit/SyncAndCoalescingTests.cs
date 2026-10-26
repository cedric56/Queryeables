using Queryeables.Abstraction;

namespace Queryeables.Tests.Unit;

public class SyncAndCoalescingTests
{
    private sealed class FakeClock(DateTime start) : TimeProvider
    {
        public DateTime Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private static readonly DateTime T0 = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);

    // ---------- SyncConventionsStore ----------

    [Fact]
    public async Task Insert_and_update_set_LastModified()
    {
        var clock = new FakeClock(T0);
        var store = new InMemoryEntityStore([]).WithSyncConventions(clock);
        var client = new TestClient { Nom = "Ana" };

        await store.InsertAsync(client);
        Assert.Equal(T0, client.LastModified);

        clock.Now = T0.AddMinutes(5);
        await store.UpdateAsync(client);
        Assert.Equal(T0.AddMinutes(5), client.LastModified);
    }

    [Fact]
    public async Task Delete_is_logical_and_hidden_from_reads()
    {
        var rows = new List<TestClient>();
        var store = new InMemoryEntityStore(rows).WithSyncConventions(new FakeClock(T0));
        var ana = new TestClient { Nom = "Ana" };
        await store.InsertAsync(ana);
        await store.InsertAsync(new TestClient { Nom = "Bob" });

        Assert.True(await store.DeleteAsync(ana));
        Assert.False(await store.DeleteAsync(ana)); // already deleted

        Assert.Equal(2, rows.Count);                 // still in the database
        Assert.True(rows.Single(r => r.Nom == "Ana").Deleted);
        Assert.Equal("Bob", Assert.Single(await store.GetAllAsync<TestClient>()).Nom);
        Assert.Null(await store.FirstOrDefaultAsync<TestClient>(c => c.Nom == "Ana"));
        Assert.Equal(1, await store.CountAsync<TestClient>(c => true));
    }

    [Fact]
    public async Task Changes_include_deleted_rows_and_respect_the_date()
    {
        var clock = new FakeClock(T0);
        var store = new InMemoryEntityStore([]).WithSyncConventions(clock);
        var ana = new TestClient { Nom = "Ana" };
        await store.InsertAsync(ana);

        clock.Now = T0.AddHours(1);
        await store.InsertAsync(new TestClient { Nom = "Bob" });
        await store.DeleteAsync(ana);

        var changes = await store.SelectChangesAsync<TestClient>(T0.AddMinutes(30));

        Assert.Equal(new[] { "Ana", "Bob" }, changes.Select(c => c.Nom).OrderBy(n => n));
        Assert.True(changes.Single(c => c.Nom == "Ana").Deleted);
    }

    // ---------- EntityCache.SyncAsync ----------

    [Fact]
    public async Task Sync_reads_only_what_changed_since_the_last_load()
    {
        var rows = new List<TestClient>
        {
            new() { Id = 1, Nom = "Ana", LastModified = T0 },
            new() { Id = 2, Nom = "Bob", LastModified = T0 },
            new() { Id = 3, Nom = "Cid", LastModified = T0 },
        };
        var log = new List<string>();
        using var cache = new EntityCache<TestClient, long>(() => new InMemoryEntityStore(rows, log), c => c.Id);
        await cache.LoadAsync();

        rows[0].Nom = "Ana modifiée"; rows[0].LastModified = T0.AddMinutes(1);
        rows[1].Deleted = true;       rows[1].LastModified = T0.AddMinutes(2);
        rows.Add(new TestClient { Id = 4, Nom = "Dan", LastModified = T0.AddMinutes(3) });
        log.Clear();

        await cache.SyncAsync();

        Assert.DoesNotContain("GETALL", log); // no full reload
        Assert.Equal("Ana modifiée", cache.Find(1)?.Nom);
        Assert.Null(cache.Find(2));
        Assert.Equal("Dan", cache.Find(4)?.Nom);
        Assert.Equal(3, cache.Entities.Count);
    }

    [Fact]
    public async Task Load_ignores_soft_deleted_rows_from_a_plain_store()
    {
        var rows = new List<TestClient>
        {
            new() { Id = 1, Nom = "Ana", LastModified = T0 },
            new() { Id = 2, Nom = "Bob", LastModified = T0, Deleted = true },
        };
        using var cache = new EntityCache<TestClient, long>(() => new InMemoryEntityStore(rows), c => c.Id);

        await cache.LoadAsync();

        Assert.Equal("Ana", Assert.Single(cache.Entities).Nom);
    }

    // ---------- Notification coalescing ----------

    [Fact]
    public async Task A_burst_of_notifications_triggers_few_refreshes()
    {
        var rows = new List<TestClient> { new() { Id = 1, Nom = "Ana", LastModified = T0 } };
        var storesCreated = 0;
        var monitor = new DbMonitor<TestClient>();
        using var cache = new EntityCache<TestClient, long>(() =>
        {
            Interlocked.Increment(ref storesCreated);
            return new InMemoryEntityStore(rows) { Delay = TimeSpan.FromMilliseconds(100) };
        }, c => c.Id, monitor);
        await cache.LoadAsync();

        var refreshes = 0;
        cache.EntitiesChanged += (_, _) => Interlocked.Increment(ref refreshes);
        for (var i = 0; i < 20; i++)
            monitor.Publish(new Record<TestClient>(1L, ChangeType.Update));

        await Task.Delay(600);

        Assert.InRange(refreshes, 1, 2);
        Assert.InRange(storesCreated, 2, 3); // initial load + one or two synchronizations, not twenty
    }

    [Fact]
    public async Task A_burst_on_one_row_reads_it_at_most_twice()
    {
        var rows = new List<TestClient> { new() { Id = 1, Nom = "Ana" } };
        var stores = new List<InMemoryEntityStore>();
        var monitor = new DbMonitor<TestClient>();
        using var cache = new EntityCache<TestClient, long>(() =>
        {
            var store = new InMemoryEntityStore(rows) { Delay = TimeSpan.FromMilliseconds(100) };
            lock (stores) stores.Add(store);
            return store;
        }, c => c.Id, monitor, RecordKey.Parse<long>);
        await cache.LoadAsync();

        for (var i = 0; i < 20; i++)
            monitor.Publish(new Record<TestClient>(1L, ChangeType.Update));
        await Task.Delay(600);

        int rowReads;
        lock (stores) rowReads = stores.Sum(s => s.SelectCalls);
        Assert.InRange(rowReads, 1, 2);
    }

    // ---------- GetAsync and bulk operations ----------

    [Fact]
    public async Task GetAsync_throws_NotFound()
    {
        var store = new InMemoryEntityStore([new() { Id = 1, Nom = "Ana" }]);

        Assert.Equal("Ana", (await store.GetAsync<TestClient>(c => c.Id == 1)).Nom);
        var error = await Assert.ThrowsAsync<EntityStoreException>(() => store.GetAsync<TestClient>(c => c.Id == 9));
        Assert.Equal(EntityErrorCode.NotFound, error.ErrorCode);
    }

    [Fact]
    public async Task InsertMany_uses_one_transaction()
    {
        var log = new List<string>();
        var store = new InMemoryEntityStore([], log);

        await store.InsertManyAsync(new[] { new TestClient { Nom = "A" }, new TestClient { Nom = "B" } });

        Assert.Equal(new[] { "BEGIN", "INSERT A", "INSERT B", "COMMIT", "DISPOSE" }, log);
    }

    [Fact]
    public async Task UpdateMany_with_a_missing_row_commits_nothing()
    {
        var log = new List<string>();
        var store = new InMemoryEntityStore([new() { Id = 1, Nom = "A" }], log);

        var error = await Assert.ThrowsAsync<EntityStoreException>(() =>
            store.UpdateManyAsync(new[] { new TestClient { Id = 1, Nom = "A2" }, new TestClient { Id = 9, Nom = "?" } }));

        Assert.Equal(EntityErrorCode.NotFound, error.ErrorCode);
        Assert.DoesNotContain("COMMIT", log);
    }

    [Fact]
    public async Task Empty_lists_do_nothing()
    {
        var log = new List<string>();
        await new InMemoryEntityStore([], log).DeleteManyAsync(Array.Empty<TestClient>());
        Assert.Empty(log);
    }
}

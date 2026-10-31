using Queryeables.Abstraction;

namespace Queryeables.Tests.Unit;

public class EntityCacheTests
{
    private readonly List<TestClient> _rows =
    [
        new() { Id = 1, Name = "Ana" },
        new() { Id = 2, Name = "Bob" },
    ];

    private readonly List<InMemoryEntityStore> _stores = [];

    private InMemoryEntityStore NewStore()
    {
        var store = new InMemoryEntityStore(_rows);
        _stores.Add(store);
        return store;
    }

    /// <summary>Change feed notifications are handled in the background: wait for the event.</summary>
    private static async Task WaitForChangeAsync(IEntityCache<TestClient, long> cache, Action trigger)
    {
        var changed = new TaskCompletionSource();
        cache.EntitiesChanged += (_, _) => changed.TrySetResult();
        trigger();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Load_fills_the_cache_and_raises_the_event()
    {
        using var cache = new EntityCache<TestClient, long>(NewStore, c => c.Id);
        var raised = 0;
        cache.EntitiesChanged += (_, _) => raised++;

        await cache.LoadAsync();

        Assert.Equal(2, cache.Entities.Count);
        Assert.Equal("Bob", cache.Find(2)?.Name);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Update_notification_reloads_only_that_row()
    {
        var monitor = new ChangeFeed<TestClient>();
        using var cache = new EntityCache<TestClient, long>(NewStore, c => c.Id, monitor, EntityKey.Parse<long>);
        await cache.LoadAsync();

        _rows[1].Name = "Bob updated";
        await WaitForChangeAsync(cache, () => monitor.Publish(new EntityChange<TestClient>(2L, ChangeType.Update)));

        Assert.Equal("Bob updated", cache.Find(2)?.Name);
        Assert.Equal(0, _stores[0].SelectCalls);       // full load: no filtered query
        Assert.Equal(1, _stores[1].SelectCalls);       // update: a single row requested
    }

    [Fact]
    public async Task Delete_notification_removes_the_row()
    {
        var monitor = new ChangeFeed<TestClient>();
        using var cache = new EntityCache<TestClient, long>(NewStore, c => c.Id, monitor, EntityKey.Parse<long>);
        await cache.LoadAsync();

        await WaitForChangeAsync(cache, () => monitor.Publish(new EntityChange<TestClient>(1L, ChangeType.Delete)));

        Assert.Null(cache.Find(1));
        Assert.Single(cache.Entities);
    }

    [Fact]
    public async Task Without_parseKey_a_notification_reloads_everything()
    {
        var monitor = new ChangeFeed<TestClient>();
        using var cache = new EntityCache<TestClient, long>(NewStore, c => c.Id, monitor);
        await cache.LoadAsync();

        _rows.Add(new TestClient { Id = 3, Name = "Cid" });
        await WaitForChangeAsync(cache, () => monitor.Publish(new EntityChange<TestClient>(3L, ChangeType.Insert)));

        Assert.Equal(3, cache.Entities.Count);
    }

    [Fact]
    public async Task Track_adds_or_replaces()
    {
        using var cache = new EntityCache<TestClient, long>(NewStore, c => c.Id);
        await cache.LoadAsync();

        cache.Track(new TestClient { Id = 2, Name = "Replaced" });
        cache.Track(new TestClient { Id = 9, Name = "Added" });

        Assert.Equal("Replaced", cache.Find(2)?.Name);
        Assert.Equal(3, cache.Entities.Count);
    }

    [Fact]
    public async Task Failed_refresh_raises_RefreshFailed_and_keeps_the_content()
    {
        var monitor = new ChangeFeed<TestClient>();
        var fail = false;
        using var cache = new EntityCache<TestClient, long>(
            () => fail ? throw new InvalidOperationException("base indisponible") : NewStore(),
            c => c.Id, monitor, EntityKey.Parse<long>);
        await cache.LoadAsync();

        var failed = new TaskCompletionSource<Exception>();
        cache.RefreshFailed += (_, ex) => failed.TrySetResult(ex);
        fail = true;
        monitor.Publish(new EntityChange<TestClient>(1L, ChangeType.Update));

        Assert.Equal("base indisponible", (await failed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Message);
        Assert.Equal(2, cache.Entities.Count);
    }
}

/// <summary>Incremental synchronization and rows that become visible late (long transactions, clocks that differ).</summary>
public class SyncOverlapTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_row_committed_late_is_caught_by_the_overlap_window()
    {
        var rows = new List<TestClient> { new() { Id = 1, Name = "Ana", LastModified = Now } };
        using var cache = new EntityCache<TestClient, long>(() => new InMemoryEntityStore(rows), c => c.Id);
        await cache.LoadAsync();

        rows.Add(new TestClient { Id = 2, Name = "Late", LastModified = Now.AddSeconds(-2) });
        await cache.SyncAsync();

        Assert.Equal("Late", cache.Find(2)?.Name);
    }

    [Fact]
    public async Task Without_an_overlap_window_a_late_row_is_missed()
    {
        var rows = new List<TestClient> { new() { Id = 1, Name = "Ana", LastModified = Now } };
        using var cache = new EntityCache<TestClient, long>(() => new InMemoryEntityStore(rows), c => c.Id) { SyncOverlap = TimeSpan.Zero };
        await cache.LoadAsync();

        rows.Add(new TestClient { Id = 2, Name = "Late", LastModified = Now.AddSeconds(-2) });
        await cache.SyncAsync();

        Assert.Null(cache.Find(2));
    }

    [Fact]
    public void The_window_never_goes_before_the_first_date()
        => Assert.Equal(DateTime.MinValue, SyncMetadata.Before(DateTime.MinValue.AddSeconds(1), TimeSpan.FromSeconds(5)));
}

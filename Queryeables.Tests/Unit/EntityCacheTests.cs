using Queryeables.Abstraction;

namespace Queryeables.Tests.Unit;

public class EntityCacheTests
{
    private readonly List<TestClient> _rows =
    [
        new() { Id = 1, Nom = "Ana" },
        new() { Id = 2, Nom = "Bob" },
    ];

    private readonly List<InMemoryEntityStore> _stores = [];

    private InMemoryEntityStore NewStore()
    {
        var store = new InMemoryEntityStore(_rows);
        _stores.Add(store);
        return store;
    }

    /// <summary>Monitor notifications are handled in the background: wait for the event.</summary>
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
        Assert.Equal("Bob", cache.Find(2)?.Nom);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Update_notification_reloads_only_that_row()
    {
        var monitor = new DbMonitor<TestClient>();
        using var cache = new EntityCache<TestClient, long>(NewStore, c => c.Id, monitor, RecordKey.Parse<long>);
        await cache.LoadAsync();

        _rows[1].Nom = "Bob modifié";
        await WaitForChangeAsync(cache, () => monitor.Publish(new Record<TestClient>(2L, ChangeType.Update)));

        Assert.Equal("Bob modifié", cache.Find(2)?.Nom);
        Assert.Equal(0, _stores[0].SelectCalls);       // full load: no filtered query
        Assert.Equal(1, _stores[1].SelectCalls);       // update: a single row requested
    }

    [Fact]
    public async Task Delete_notification_removes_the_row()
    {
        var monitor = new DbMonitor<TestClient>();
        using var cache = new EntityCache<TestClient, long>(NewStore, c => c.Id, monitor, RecordKey.Parse<long>);
        await cache.LoadAsync();

        await WaitForChangeAsync(cache, () => monitor.Publish(new Record<TestClient>(1L, ChangeType.Delete)));

        Assert.Null(cache.Find(1));
        Assert.Single(cache.Entities);
    }

    [Fact]
    public async Task Without_parseKey_a_notification_reloads_everything()
    {
        var monitor = new DbMonitor<TestClient>();
        using var cache = new EntityCache<TestClient, long>(NewStore, c => c.Id, monitor);
        await cache.LoadAsync();

        _rows.Add(new TestClient { Id = 3, Nom = "Cid" });
        await WaitForChangeAsync(cache, () => monitor.Publish(new Record<TestClient>(3L, ChangeType.Insert)));

        Assert.Equal(3, cache.Entities.Count);
    }

    [Fact]
    public async Task Track_adds_or_replaces()
    {
        using var cache = new EntityCache<TestClient, long>(NewStore, c => c.Id);
        await cache.LoadAsync();

        cache.Track(new TestClient { Id = 2, Nom = "Remplacé" });
        cache.Track(new TestClient { Id = 9, Nom = "Ajouté" });

        Assert.Equal("Remplacé", cache.Find(2)?.Nom);
        Assert.Equal(3, cache.Entities.Count);
    }

    [Fact]
    public async Task Failed_refresh_raises_RefreshFailed_and_keeps_the_content()
    {
        var monitor = new DbMonitor<TestClient>();
        var fail = false;
        using var cache = new EntityCache<TestClient, long>(
            () => fail ? throw new InvalidOperationException("base indisponible") : NewStore(),
            c => c.Id, monitor, RecordKey.Parse<long>);
        await cache.LoadAsync();

        var failed = new TaskCompletionSource<Exception>();
        cache.RefreshFailed += (_, ex) => failed.TrySetResult(ex);
        fail = true;
        monitor.Publish(new Record<TestClient>(1L, ChangeType.Update));

        Assert.Equal("base indisponible", (await failed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Message);
        Assert.Equal(2, cache.Entities.Count);
    }
}

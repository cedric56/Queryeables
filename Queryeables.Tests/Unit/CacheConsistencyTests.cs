using Queryeables.Abstraction;

namespace Queryeables.Tests.Unit;

/// <summary>What a cache holds when writes, reads and failures cross: the rows this application wrote, copies, freshness.</summary>
public class CacheConsistencyTests
{
    private readonly MemoryDatabase _database = new();

    private IEntityStoreFactory Stores => new EntityStoreFactory(() => new MemoryStore(_database));

    [Fact]
    public async Task A_key_that_cannot_be_parsed_synchronizes_the_cache()
    {
        var rows = new List<TestClient> { new() { Id = 1, Name = "Ana" } };
        var monitor = new ChangeFeed<TestClient>();
        using var cache = new EntityCache<TestClient, long>(() => new InMemoryEntityStore(rows), c => c.Id, monitor,
            EntityKey.Parse<long>);
        await cache.LoadAsync();
        Exception? failure = null;
        cache.RefreshFailed += (_, ex) => failure = ex;

        rows.Add(new TestClient { Id = 2, Name = "Bob" });
        var changed = new TaskCompletionSource();
        cache.EntitiesChanged += (_, _) => changed.TrySetResult();
        monitor.Publish(new EntityChange<TestClient>("not-a-number", ChangeType.Insert));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(failure);
        Assert.Equal("Bob", cache.Find(2)?.Name);
    }

    [Fact]
    public async Task A_row_tracked_while_the_cache_reads_its_table_is_kept()
    {
        var rows = new List<TestClient> { new() { Id = 1, Name = "Ana" } };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocking = false;
        using var cache = new EntityCache<TestClient, long>(() =>
        {
            if (blocking)
            {
                entered.Set();
                release.Wait();                 // the table is read as it was before the write below
            }
            return new InMemoryEntityStore(rows);
        }, c => c.Id);
        await cache.LoadAsync();

        blocking = true;
        var load = Task.Run(() => cache.LoadAsync());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        cache.Track(new TestClient { Id = 2, Name = "Bob" });      // written meanwhile, by this client
        release.Set();
        await load;

        Assert.Equal("Bob", cache.Find(2)?.Name);
    }

    [Fact]
    public async Task A_row_tracked_while_the_cache_reads_it_again_is_kept()
    {
        var rows = new List<TestClient> { new() { Id = 1, Name = "Before" } };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocking = false;
        var feed = new ChangeFeed<TestClient>();
        using var cache = new EntityCache<TestClient, long>(() =>
        {
            if (blocking)
            {
                entered.Set();
                release.Wait();                 // the row is read as it was before the write below
            }
            return new InMemoryEntityStore(rows);
        }, c => c.Id, feed);
        await cache.LoadAsync();

        blocking = true;
        var notified = Task.Run(() => feed.Publish(new EntityChange<TestClient>("1", ChangeType.Update)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        cache.Track(new TestClient { Id = 1, Name = "Mine" });              // saved by this client meanwhile
        release.Set();
        await notified;
        await Task.Delay(100);

        Assert.Equal("Mine", cache.Find(1)?.Name);
    }

    [Fact]
    public async Task A_saved_entity_changed_afterwards_without_a_save_is_not_changed_in_the_cache()
    {
        var writes = new LocalWrites();
        using var cache = new EntityCache<TestClient, long>(Stores, c => c.Id, localWrites: writes);
        await using var store = new LocalWritesDecorator(new MemoryStore(_database), writes);

        var ana = new TestClient { Name = "Ana" };
        await store.InsertAsync(ana);
        ana.Name = "Ana (not saved)";                                           // a form cancelled

        var cached = cache.Find(ana.Id) ?? throw new InvalidOperationException("Not in the cache.");
        Assert.False(ReferenceEquals(ana, cached));
        Assert.Equal("Ana", cached.Name);
    }

    [Fact]
    public async Task A_saved_entity_is_cached_without_its_loaded_children()
    {
        var writes = new LocalWrites();
        using var cache = new EntityCache<Invoice, long>(Stores, o => o.Id, localWrites: writes);
        await using var store = new LocalWritesDecorator(new MemoryStore(_database), writes);

        var order = new Invoice { Customer = "Ana", Lines = [new InvoiceLine { Product = "Tea" }] };
        await store.InsertAsync(order);

        var cached = cache.Find(order.Id) ?? throw new InvalidOperationException("Not in the cache.");
        Assert.Equal("Ana", cached.Customer);
        Assert.Null(cached.Lines);                                        // as a row read from the database
    }

    private static bool _down;

    private static IEntityStore Flaky(List<TestClient> rows)
        => _down ? throw new HttpRequestException("No network.") : new InMemoryEntityStore(rows);

    [Fact]
    public async Task A_cache_says_when_its_last_read_failed_and_when_it_last_succeeded()
    {
        var rows = new List<TestClient> { new() { Id = 1, Name = "Ana" } };
        using var cache = new EntityCache<TestClient, long>(() => Flaky(rows), c => c.Id);
        Assert.Equal(CacheFreshness.NotLoaded, cache.Freshness);

        _down = false;
        await cache.LoadAsync();
        Assert.Equal(CacheFreshness.Current, cache.Freshness);
        var loadedAt = cache.LastRefreshedAt;
        Assert.NotNull(loadedAt);

        _down = true;
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => cache.SyncAsync());
            Assert.Equal(CacheFreshness.Stale, cache.Freshness);
            Assert.IsType<HttpRequestException>(cache.LastError);
            Assert.Equal(loadedAt, cache.LastRefreshedAt);
            Assert.Equal("Ana", cache.Find(1)?.Name);                         // the previous content is kept
        }
        finally
        {
            _down = false;
        }

        await cache.SyncAsync();
        Assert.Equal(CacheFreshness.Current, cache.Freshness);
        Assert.Null(cache.LastError);

        await cache.ClearUserDataAsync();
        Assert.Equal(CacheFreshness.NotLoaded, cache.Freshness);
        Assert.Null(cache.LastRefreshedAt);
    }
}

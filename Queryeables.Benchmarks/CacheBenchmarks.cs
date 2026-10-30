using BenchmarkDotNet.Attributes;
using Queryeables.Abstraction;

namespace Queryeables.Benchmarks;

/// <summary>What a cache saves: reading one entity from <see cref="EntityCache{TEntity, TKey}"/> instead of the database.</summary>
[MemoryDiagnoser]
public class CacheBenchmarks
{
    private BenchmarkDatabase _database = null!;
    private EntityCache<Item, long> _cache = null!;
    private long _id;

    [Params(1000, 100_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _database = await BenchmarkDatabase.CreateAsync("cache-benchmarks", Rows);
        _cache = new EntityCache<Item, long>(_database.Stores, i => i.Id);
        await _cache.LoadAsync();
        _id = Rows / 2;
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _cache.Dispose();
        await _database.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public async Task<Item?> Store_FirstOrDefault()
    {
        await using var store = _database.Stores.Create();
        return await store.FirstOrDefaultAsync<Item>(i => i.Id == _id);
    }

    [Benchmark]
    public Item? Cache_Find() => _cache.Find(_id);

    /// <summary>A snapshot of every entity, safe to enumerate while the cache changes.</summary>
    [Benchmark]
    public int Cache_Entities() => _cache.Entities.Count;

    /// <summary>A full reload, as after a change without a key or without [LastModified].</summary>
    [Benchmark]
    public Task Cache_Load() => _cache.LoadAsync();
}

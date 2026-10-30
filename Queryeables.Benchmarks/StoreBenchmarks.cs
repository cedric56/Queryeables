using BenchmarkDotNet.Attributes;
using Dommel;
using Queryeables.Abstraction;

namespace Queryeables.Benchmarks;

/// <summary>
/// What <c>IEntityStore</c> costs on top of Dommel: the same query, written with Dommel on a connection (baseline) and
/// through <see cref="Dommel.DommelEntityStore"/>. Each invocation opens its connection, like an application does.
/// </summary>
[MemoryDiagnoser]
public class StoreBenchmarks
{
    private BenchmarkDatabase _database = null!;
    private long _id;

    [Params(1000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _database = await BenchmarkDatabase.CreateAsync("store-benchmarks", Rows);
        _id = Rows / 2;
    }

    [GlobalCleanup]
    public Task CleanupAsync() => _database.DisposeAsync().AsTask();

    [Benchmark(Baseline = true)]
    public async Task<Item?> Dommel_FirstOrDefault()
    {
        await using var connection = await _database.OpenAsync();
        return await connection.FirstOrDefaultAsync<Item>(i => i.Id == _id);
    }

    [Benchmark]
    public async Task<Item?> Store_FirstOrDefault()
    {
        await using var store = _database.Stores.Create();
        return await store.FirstOrDefaultAsync<Item>(i => i.Id == _id);
    }

    [Benchmark]
    public async Task<int> Dommel_GetAll()
    {
        await using var connection = await _database.OpenAsync();
        return (await connection.GetAllAsync<Item>()).Count();
    }

    [Benchmark]
    public async Task<int> Store_GetAll()
    {
        await using var store = _database.Stores.Create();
        return (await store.GetAllAsync<Item>()).Count;
    }

    [Benchmark]
    public async Task Dommel_InsertDelete()
    {
        await using var connection = await _database.OpenAsync();
        var item = new Item { Name = "new", Quantity = 1 };
        item.Id = Convert.ToInt64(await connection.InsertAsync(item));
        await connection.DeleteAsync(item);
    }

    /// <summary>The same writes, in a transaction that reports a <see cref="DbResult"/>.</summary>
    [Benchmark]
    public async Task<DbResult> Store_InsertDelete_InTransaction()
    {
        await using var store = _database.Stores.Create();
        return await store.RunInTransactionAsync(async s =>
        {
            var item = new Item { Name = "new", Quantity = 1 };
            await s.InsertAsync(item);
            await s.DeleteAsync(item);
        });
    }
}

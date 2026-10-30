using BenchmarkDotNet.Attributes;
using Queryeables.Abstraction;
using Queryeables.Onion;
using Queryeables.Remote;

namespace Queryeables.Benchmarks;

/// <summary>
/// What the remote layer costs without the network: the client store, predicate translation, MessagePack, access policy
/// and the server dispatcher, all in process (<see cref="EntityRequestDispatcher.AsRemote"/>). Add the round trip of your
/// network to these numbers for a real deployment.
/// </summary>
[MemoryDiagnoser]
public class RemoteBenchmarks
{
    private BenchmarkDatabase _database = null!;
    private IEntityStoreFactory _remote = null!;
    private long _id;

    [Params(1000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _database = await BenchmarkDatabase.CreateAsync("remote-benchmarks", Rows);
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<Item>(_database.Stores, EntityAccessPolicies.AllowAll));
        var dispatcher = new EntityRequestDispatcher(registry, MessagePackEntitySerializer.Default, _database.Stores);
        _remote = RemoteEntityStore.Factory(dispatcher.AsRemote(), MessagePackEntitySerializer.Default);
        _id = Rows / 2;
    }

    [GlobalCleanup]
    public Task CleanupAsync() => _database.DisposeAsync().AsTask();

    [Benchmark(Baseline = true)]
    public async Task<Item?> Local_FirstOrDefault()
    {
        await using var store = _database.Stores.Create();
        return await store.FirstOrDefaultAsync<Item>(i => i.Id == _id);
    }

    [Benchmark]
    public async Task<Item?> Remote_FirstOrDefault()
    {
        await using var store = _remote.Create();
        return await store.FirstOrDefaultAsync<Item>(i => i.Id == _id);
    }

    [Benchmark]
    public async Task<int> Local_GetAll()
    {
        await using var store = _database.Stores.Create();
        return (await store.GetAllAsync<Item>()).Count;
    }

    [Benchmark]
    public async Task<int> Remote_GetAll()
    {
        await using var store = _remote.Create();
        return (await store.GetAllAsync<Item>()).Count;
    }

    /// <summary>Two writes queued on the client and sent in one batch, applied in one server transaction.</summary>
    [Benchmark]
    public async Task<DbResult> Remote_InsertDelete_InTransaction()
    {
        await using var store = _remote.Create();
        var item = new Item { Name = "new", Quantity = 1 };
        var inserted = await store.RunInTransactionAsync(s => s.InsertAsync(item));
        if (!inserted.Successful)
            return inserted;
        return await store.RunInTransactionAsync(async s => { await s.DeleteAsync(item); });
    }
}

using BenchmarkDotNet.Attributes;
using Queryeables.Abstraction;
using Queryeables.Remote;

namespace Queryeables.Benchmarks;

/// <summary>
/// The same calls three ways: on the database directly, through the remote layer in process (<see cref="RemoteBenchmarks"/>),
/// and through a real gRPC connection to a server (<see cref="LoopbackServer"/>). <c>Grpc_*</c> minus <c>InProcess_*</c> is
/// what HTTP/2, gRPC and MagicOnion cost on a loopback; a real network adds its round trip on top.
/// </summary>
[MemoryDiagnoser]
public class NetworkBenchmarks
{
    private const int Parallel = 16;

    private BenchmarkDatabase _database = null!;
    private LoopbackServer _server = null!;
    private IEntityStoreFactory _inProcess = null!;
    private IEntityStoreFactory _grpc = null!;
    private long _id;

    [Params(1000)]
    public int Rows { get; set; }

    /// <summary>The format of the entities on the wire, on both the in-process and the gRPC paths.</summary>
    [Params(WireFormat.Json, WireFormat.MessagePack)]
    public WireFormat Format { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _database = await BenchmarkDatabase.CreateAsync("network-benchmarks", Rows);
        _server = await LoopbackServer.StartAsync(_database.Stores, Format);
        _grpc = _server.Client();

        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<Item>(_database.Stores, EntityAccessPolicies.AllowAll));
        var dispatcher = new EntityRequestDispatcher(registry, Format.Serializer(server: true));
        _inProcess = RemoteEntityStore.Factory(dispatcher.AsTransport(), Format.Serializer());
        _id = Rows / 2;

        // The first call opens the HTTP/2 connection: not measured.
        if (await FirstOrDefault(_grpc) is null)
            throw new InvalidOperationException($"The server at {_server.Url} did not return item {_id}.");
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _server.DisposeAsync();
        await _database.DisposeAsync();
    }

    private async Task<Item?> FirstOrDefault(IEntityStoreFactory stores)
    {
        await using var store = stores.Create();
        return await store.FirstOrDefaultAsync<Item>(i => i.Id == _id);
    }

    private static async Task<int> GetAll(IEntityStoreFactory stores)
    {
        await using var store = stores.Create();
        return (await store.GetAllAsync<Item>()).Count;
    }


    [Benchmark(Baseline = true)]
    public Task<Item?> Local_FirstOrDefault() => FirstOrDefault(_database.Stores);

    [Benchmark]
    public Task<Item?> InProcess_FirstOrDefault() => FirstOrDefault(_inProcess);

    [Benchmark]
    public Task<Item?> Grpc_FirstOrDefault() => FirstOrDefault(_grpc);

    [Benchmark]
    public Task<int> Local_GetAll() => GetAll(_database.Stores);

    [Benchmark]
    public Task<int> InProcess_GetAll() => GetAll(_inProcess);

    [Benchmark]
    public Task<int> Grpc_GetAll() => GetAll(_grpc);


    /// <summary>
    /// <see cref="Parallel"/> reads at once on one connection (HTTP/2 multiplexes them): the time per read, under load.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Parallel)]
    public Task Grpc_FirstOrDefault_Parallel() => Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ => FirstOrDefault(_grpc)));
}

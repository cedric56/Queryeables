using Queryeables.Abstraction;
using Queryeables.Remote;
using System.Security.Claims;

namespace Queryeables.Tests.Unit;

/// <summary>A write sent again with the same operation id is applied once, for its user only.</summary>
public class IdempotencyTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();
    private readonly EntityRequestDispatcher _dispatcher;

    public IdempotencyTests()
    {
        var stores = new EntityStoreFactory(() => new MemoryStore(_database).WithSyncConventions());
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<TestClient>(stores, EntityAccessPolicies.AllowAll));
        _dispatcher = new EntityRequestDispatcher(registry, _serializer, stores);
    }

    private static EntityRequestContext As(string user)
        => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test")));

    private byte[] Client(string name) => _serializer.Serialize(new TestClient { Name = name });

    [Fact]
    public async Task The_same_operation_sent_twice_is_applied_once_and_answered_the_same()
    {
        var id = Guid.NewGuid();
        var first = await _dispatcher.WriteOnceAsync(id, EntityName.Of<TestClient>(), BatchOperationKind.Insert, Client("Ana"), As("ana"));
        var again = await _dispatcher.WriteOnceAsync(id, EntityName.Of<TestClient>(), BatchOperationKind.Insert, Client("Ana"), As("ana"));

        Assert.True(first.Successful);
        Assert.Equal(first.Data, again.Data);
        Assert.Single(_database.All<TestClient>());
    }

    [Fact]
    public async Task Concurrent_copies_of_one_operation_are_applied_once()
    {
        var id = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            _dispatcher.WriteOnceAsync(id, EntityName.Of<TestClient>(), BatchOperationKind.Insert, Client("Ana"), As("ana")))));

        Assert.All(results, r => Assert.True(r.Successful));
        Assert.Single(_database.All<TestClient>());
    }

    [Fact]
    public async Task An_operation_id_belongs_to_its_user()
    {
        var id = Guid.NewGuid();
        await _dispatcher.WriteOnceAsync(id, EntityName.Of<TestClient>(), BatchOperationKind.Insert, Client("Ana's"), As("ana"));
        var bob = await _dispatcher.WriteOnceAsync(id, EntityName.Of<TestClient>(), BatchOperationKind.Insert, Client("Bob's"), As("bob"));

        Assert.Contains("Bob's", _serializer.Deserialize<TestClient>(bob.Data!)!.Name);      // not Ana's answer
        Assert.Equal(2, _database.All<TestClient>().Count);
    }

    [Fact]
    public async Task A_batch_sent_again_with_its_id_is_applied_once()
    {
        var id = Guid.NewGuid();
        BatchOperation[] batch = [new(EntityName.Of<TestClient>(), BatchOperationKind.Insert, Client("Ana"), null),
                                  new(EntityName.Of<TestClient>(), BatchOperationKind.Insert, Client("Bob"), null)];
        var first = await _dispatcher.ExecuteBatchOnceAsync(id, batch, As("ana"));
        var again = await _dispatcher.ExecuteBatchOnceAsync(id, batch, As("ana"));

        Assert.True(first.Successful);
        Assert.Equal(first.Data, again.Data);
        Assert.Equal(2, _database.All<TestClient>().Count);
        Assert.Equal(EntityErrorCode.InvalidRequest, (await _dispatcher.ExecuteBatchOnceAsync(Guid.Empty, batch, As("ana"))).ErrorCode);
    }

    [Fact]
    public async Task A_commit_whose_answer_is_lost_is_sent_again_and_applied_once()
    {
        var transport = new LosingTransport(_dispatcher.AsTransport(As("ana")));
        await using var store = new RemoteEntityStore(transport, _serializer);
        var ana = new TestClient { Name = "Ana" };

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await store.InsertAsync(ana);
            transport.LoseNextAnswer = true;
            Assert.True((await transaction.CommitAsync()).Successful);
        }

        Assert.Equal(2, transport.BatchCalls);                 // the first answer was lost
        Assert.Single(_database.All<TestClient>());
        Assert.NotEqual(0, ana.Id);                            // the key of the first, and only, insert
    }

    [Fact]
    public async Task A_transport_without_operation_ids_sends_a_plain_batch_once()
    {
        var transport = new LosingTransport(_dispatcher.AsTransport(As("ana"))) { CarriesIds = false };
        await using var store = new RemoteEntityStore(transport, _serializer);

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await store.InsertAsync(new TestClient { Name = "Ana" });
            Assert.True((await transaction.CommitAsync()).Successful);
        }

        Assert.Equal(1, transport.PlainBatches);
        Assert.Single(_database.All<TestClient>());
    }

    /// <summary>A network that delivers a batch to the server, then may lose its answer.</summary>
    private sealed class LosingTransport(IEntityTransport inner) : IEntityTransport
    {
        public bool LoseNextAnswer { get; set; }
        public bool CarriesIds { get; init; } = true;
        public int BatchCalls { get; private set; }
        public int PlainBatches { get; private set; }

        public async Task<TransportResult> ExecuteBatchOnceAsync(Guid operationId, BatchOperation[] operations, CancellationToken ct = default)
        {
            if (!CarriesIds)
                return TransportResult.Failure(EntityErrorCode.NotSupported, "No ids.");
            BatchCalls++;
            var result = await inner.ExecuteBatchOnceAsync(operationId, operations, ct);
            if (LoseNextAnswer)
            {
                LoseNextAnswer = false;
                throw new IOException("The connection was reset.");
            }
            return result;
        }

        public Task<TransportResult> ExecuteBatchAsync(BatchOperation[] operations, CancellationToken ct = default)
        {
            PlainBatches++;
            return inner.ExecuteBatchAsync(operations, ct);
        }

        public Task<TransportResult> GetAllAsync(string type, CancellationToken ct = default) => inner.GetAllAsync(type, ct);
        public Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, bool any, CancellationToken ct = default) => inner.SelectAsync(type, conditions, any, ct);
        public Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize, CancellationToken ct = default)
            => inner.SelectPagedAsync(type, conditions, any, pageNumber, pageSize, ct);
        public Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default) => inner.GetChangesAsync(type, sinceUtc, ct);
        public Task<TransportResult> InsertAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default) => inner.InsertAsync(type, entity, connectionId, ct);
        public Task<TransportResult> UpdateAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default) => inner.UpdateAsync(type, entity, connectionId, ct);
        public Task<TransportResult> DeleteAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default) => inner.DeleteAsync(type, entity, connectionId, ct);
    }

    [Fact]
    public async Task A_refused_operation_is_not_remembered_and_is_checked_again()
    {
        var invalid = await _dispatcher.WriteOnceAsync(Guid.Empty, EntityName.Of<TestClient>(), BatchOperationKind.Insert, Client("X"), As("ana"));
        Assert.Equal(EntityErrorCode.InvalidRequest, invalid.ErrorCode);

        var calls = 0;
        var log = new MemoryIdempotencyStore();
        var failure = TransportResult.Failure(EntityErrorCode.Conflict, "stale");
        await log.RunOnceAsync("k", () => { calls++; return Task.FromResult(failure); });
        await log.RunOnceAsync("k", () => { calls++; return Task.FromResult(failure); });
        Assert.Equal(2, calls);
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public async Task Results_are_forgotten_after_the_retention()
    {
        var time = new ManualTime();
        var log = new MemoryIdempotencyStore(time) { Retention = TimeSpan.FromHours(1) };
        var calls = 0;
        Task<TransportResult> Write() { calls++; return Task.FromResult(TransportResult.Success([1])); }

        await log.RunOnceAsync("k", Write);
        await log.RunOnceAsync("k", Write);
        time.Now += TimeSpan.FromHours(2);
        await log.RunOnceAsync("k", Write);

        Assert.Equal(2, calls);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

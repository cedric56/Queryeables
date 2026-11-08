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

using System.Collections.Concurrent;
using System.Security.Claims;
using Queryeables.Abstraction;
using Queryeables.Remote;
using Queryeables.Remote.Offline;

namespace Queryeables.Tests.Unit;

/// <summary>The fixes of the code review of November 14, each against the way it was found.</summary>
public class CodeReviewAuditTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();

    private IEntityStoreFactory Stores => new EntityStoreFactory(() => new MemoryStore(_database));

    // ---------- The offline queue survives two stops in a row ----------

    private sealed class StoppingStorage(MemoryOfflineStorage inner) : IOfflineStorage
    {
        public bool StopBeforeIndex { get; set; }

        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken ct = default) => inner.ReadAsync(key, ct);

        public ValueTask WriteAsync(string key, byte[] value, CancellationToken ct = default)
            => StopBeforeIndex && key.EndsWith("outbox/index", StringComparison.Ordinal)
                ? throw new IOException("The device stopped.")
                : inner.WriteAsync(key, value, ct);

        public ValueTask DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
    }

    [Fact]
    public async Task A_write_recovered_after_a_stop_survives_a_second_stop_before_the_index()
    {
        var storage = new StoppingStorage(new MemoryOfflineStorage()) { StopBeforeIndex = true };

        var first = new Outbox();
        first.Add(BatchOperationKind.Insert, "T", "-1", [1]);
        await Assert.ThrowsAsync<IOException>(() => first.SaveAsync(storage, "p", default));   // staged, not indexed

        var second = new Outbox();
        await second.LoadAsync(storage, "p", default);                                          // recovered
        second.Add(BatchOperationKind.Insert, "T", "-2", [2]);
        await Assert.ThrowsAsync<IOException>(() => second.SaveAsync(storage, "p", default));  // stops again

        storage.StopBeforeIndex = false;
        var third = new Outbox();
        await third.LoadAsync(storage, "p", default);
        Assert.Equal(["-1", "-2"], third.Snapshot().Select(e => e.Key));
    }

    // ---------- The cache keeps what this application wrote while it read a row ----------

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

    // ---------- The access policy sees the stored row of an update or a delete ----------

    private static EntityRequestContext As(string user)
        => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test")));

    [Fact]
    public async Task An_update_cannot_go_round_a_policy_with_the_content_it_sends()
    {
        var locked = new TestClient { Name = "Locked" };
        await new MemoryStore(_database).InsertAsync(locked);
        var policy = EntityAccessPolicies.From(r => r.Operation == EntityOperation.Read || r.Entity is TestClient { Name: not "Locked" });
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<TestClient>(Stores, policy));
        await using var store = new RemoteEntityStore(new EntityRequestDispatcher(registry, _serializer, Stores).AsTransport(As("ana")), _serializer);

        var claimed = EntityValues.Copy(locked);
        claimed.Name = "Free";                                                // what the client claims, to pass the policy

        Assert.Equal(EntityErrorCode.Forbidden, (await Assert.ThrowsAsync<EntityStoreException>(() => store.UpdateAsync(claimed))).ErrorCode);
        Assert.Equal(EntityErrorCode.Forbidden, (await Assert.ThrowsAsync<EntityStoreException>(() => store.DeleteAsync(claimed))).ErrorCode);
        Assert.Equal("Locked", Assert.Single(_database.All<TestClient>()).Name);
    }

    // ---------- A visibility rule sends to the connections it checked, not to all but the others ----------

    private sealed class Targeted(IChangeFeed<TestClient> monitor, ChangeHubOptions options) : ChangeBroadcaster<TestClient>(monitor, options: options)
    {
        public ConcurrentQueue<string> Sent { get; } = new();

        protected override Task SendAsync(EntityChange<TestClient> record, IReadOnlyList<string> excludedConnections, CancellationToken ct)
        {
            Sent.Enqueue($"{record.EntityKey}: all except {string.Join(",", excludedConnections)}");
            return Task.CompletedTask;
        }

        protected override Task SendToAsync(EntityChange<TestClient> record, IReadOnlyList<string> recipients, CancellationToken ct)
        {
            Sent.Enqueue($"{record.EntityKey}: only {string.Join(",", recipients)}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task With_a_visibility_rule_a_change_goes_only_to_the_connections_it_accepts()
    {
        var options = new ChangeHubOptions
        {
            Visibility = (audience, _) => ValueTask.FromResult(audience.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value == "ana"),
        };
        using var broadcaster = new Targeted(new ChangeFeed<TestClient>(), options);
        broadcaster.AddSubscriber("c-ana", As("ana").User);
        broadcaster.AddSubscriber("c-bob", As("bob").User);

        broadcaster.Relay(new EntityChange<TestClient>("7", ChangeType.Update));
        broadcaster.Relay(new EntityChange<TestClient>(string.Empty, ChangeType.None));

        for (var i = 0; i < 300 && broadcaster.Sent.Count < 2; i++)
            await Task.Delay(10);
        Assert.Equal(["7: only c-ana", ": all except "], broadcaster.Sent.ToArray());
    }

    // ---------- A cleanup that fails never fails a write applied ----------

    private sealed class TrimFailingStore(MemoryStore inner) : IEntityStore
    {
        public Task<IReadOnlyList<T>> GetAllAsync<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(EntityMembers.Required)] T>(CancellationToken ct = default) where T : class => inner.GetAllAsync<T>(ct);
        public Task<IReadOnlyList<T>> SelectAsync<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(EntityMembers.Required)] T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class => inner.SelectAsync(predicate, ct);
        public Task<T?> FirstOrDefaultAsync<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(EntityMembers.Required)] T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class => inner.FirstOrDefaultAsync(predicate, ct);
        public Task<IReadOnlyList<T>> SelectPagedAsync<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(EntityMembers.Required)] T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, int pageNumber, int pageSize, CancellationToken ct = default) where T : class => inner.SelectPagedAsync(predicate, pageNumber, pageSize, ct);
        public Task InsertAsync<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(EntityMembers.Required)] T>(T entity, CancellationToken ct = default) where T : class => inner.InsertAsync(entity, ct);
        public Task<bool> UpdateAsync<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(EntityMembers.Required)] T>(T entity, CancellationToken ct = default) where T : class => inner.UpdateAsync(entity, ct);
        public Task<bool> DeleteAsync<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(EntityMembers.Required)] T>(T entity, CancellationToken ct = default) where T : class => inner.DeleteAsync(entity, ct);
        public Task<int> DeleteWhereAsync<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(EntityMembers.Required)] T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class
            => throw new InvalidOperationException("The database failed during the cleanup.");
        public Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default) => inner.BeginTransactionAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public async Task A_failed_cleanup_does_not_turn_an_applied_write_into_a_failure()
    {
        var errors = new ConcurrentQueue<Exception>();
        var store = new StoreIdempotencyStore(new EntityStoreFactory(() => new TrimFailingStore(new MemoryStore(_database))))
        {
            OnError = errors.Enqueue,
        };

        for (var i = 0; i < 1024; i++)                                        // the 1024th write triggers a cleanup
            Assert.True((await store.RunOnceAsync($"k{i}", () => Task.FromResult(TransportResult.Success()))).Successful);

        for (var i = 0; i < 100 && errors.IsEmpty; i++)
            await Task.Delay(10);
        Assert.Single(errors);
    }
}

using System.Collections.Concurrent;
using System.Security.Claims;
using Queryeables.Abstraction;
using Queryeables.Remote;

namespace Queryeables.Tests.Unit;

/// <summary>The fixes of the code review of November 14, each against the way it was found.</summary>
public class CodeReviewAuditTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();

    private IEntityStoreFactory Stores => new EntityStoreFactory(() => new MemoryStore(_database));

    // ---------- The offline queue survives two stops in a row ----------

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

}

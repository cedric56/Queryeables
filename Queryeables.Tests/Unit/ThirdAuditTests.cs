using System.Collections.Concurrent;
using System.Security.Claims;
using Queryeables.Abstraction;
using Queryeables.PostgreSql;
using Queryeables.Remote;
using Queryeables.Remote.Offline;

namespace Queryeables.Tests.Unit;

/// <summary>The fixes of the audit of commit 7ee8e22, each against the way it was found.</summary>
public class ThirdAuditTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();

    private IEntityStoreFactory Stores => new EntityStoreFactory(() => new MemoryStore(_database));

    private static EntityRequestContext As(string user)
        => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test")));

    private RemoteEntityStore Client(EntityHandler<TestClient> handler, string user = "ana")
    {
        var registry = new EntityHandlerRegistry();
        registry.Register(handler);
        return new RemoteEntityStore(new EntityRequestDispatcher(registry, _serializer, Stores).AsTransport(As(user)), _serializer);
    }

    private async Task<TestClient> SeedAsync(string name, bool active = false)
    {
        var client = new TestClient { Name = name, Active = active };
        await new MemoryStore(_database).InsertAsync(client);
        return client;
    }

    /// <summary>Updates allowed only on the rows that are not active (what the policy reads from the entity it is given).</summary>
    private sealed class InactiveOnly : IEntityAccessPolicy
    {
        public ValueTask<bool> IsAllowedAsync(EntityAccessRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(request.Operation != EntityOperation.Update || request.Entity is TestClient { Active: false });
    }

    // ---------- 1. A patch is checked against the stored row, not only the client's content ----------

    [Fact]
    public async Task A_patch_cannot_go_round_a_policy_with_the_content_it_sends()
    {
        var stored = await SeedAsync("Ana", active: true);
        await using var store = Client(new EntityHandler<TestClient>(Stores, new InactiveOnly()));

        var original = EntityValues.Copy(stored);
        original.Active = false;                                    // what the client claims, to pass the policy
        var modified = EntityValues.Copy(original);
        modified.Name = "Hacked";

        var denied = await Assert.ThrowsAsync<EntityStoreException>(() => store.PatchAsync(original, modified));
        Assert.Equal(EntityErrorCode.Forbidden, denied.ErrorCode);
        Assert.Equal("Ana", Assert.Single(_database.All<TestClient>()).Name);
    }

    // ---------- 2. The extended filters are bounded as the plain ones ----------

    [Fact]
    public void Lists_of_values_count_toward_the_limit_of_a_filter()
    {
        var big = Enumerable.Range(0, Filters.MaxTotalValues / 2 + 1).Cast<object>().ToList();
        var conditions = new[]
        {
            new FilterCondition("Id", FilterOperator.In, big),
            new FilterCondition("Id", FilterOperator.In, big),
        };
        Assert.Throws<ArgumentException>(() => Filters.CheckSize(conditions));
        Filters.CheckSize([new FilterCondition("Id", FilterOperator.In, big)]);
    }

    // ---------- 3. An aggregate computed in memory is bounded ----------

    [Fact]
    public async Task An_aggregate_a_store_computes_in_memory_is_refused_beyond_the_read_limit()
    {
        await SeedAsync("Ana");
        await SeedAsync("Bob");
        await SeedAsync("Carl");
        await using var store = Client(new EntityHandler<TestClient>(Stores, EntityAccessPolicies.AllowAll, maxReadSize: 2));

        var refused = await Assert.ThrowsAsync<EntityStoreException>(
            () => store.AggregateAsync<TestClient>(c => c.Id > 0, Aggregation.Of<TestClient>().Count()));
        Assert.Equal(EntityErrorCode.InvalidRequest, refused.ErrorCode);

        var one = Assert.Single(await store.AggregateAsync<TestClient>(c => c.Name == "Ana", Aggregation.Of<TestClient>().Count()));
        Assert.Equal(1, one.Count);
    }

    // ---------- 7. The offline queue keeps the order of writes on different rows ----------

    [Fact]
    public void A_write_is_not_merged_over_a_write_on_another_row()
    {
        var outbox = new Outbox();
        outbox.Add(BatchOperationKind.Update, "Customer", "1", [1]);
        outbox.Add(BatchOperationKind.Insert, "Order", "-1", [2]);         // refers to customer 1 as updated
        outbox.Add(BatchOperationKind.Update, "Customer", "1", [3]);       // then customer 1 refers to the order

        Assert.Equal([("Customer", "1"), ("Order", "-1"), ("Customer", "1")],
            outbox.Snapshot().Select(e => (e.Type, e.Key)));

        outbox.Add(BatchOperationKind.Update, "Customer", "1", [4]);       // right after its own write: merged
        Assert.Equal(3, outbox.Count);
        Assert.Equal([4], outbox.Snapshot()[^1].Payload);
    }

    // ---------- 9. A change reaches only the users who may see its row ----------

    private sealed class Recording(IChangeFeed<TestClient> monitor, ChangeHubOptions options) : ChangeBroadcaster<TestClient>(monitor, options: options)
    {
        public ConcurrentQueue<(string Key, string[] Excluded)> Sent { get; } = new();

        protected override Task SendAsync(EntityChange<TestClient> record, IReadOnlyList<string> excludedConnections, CancellationToken ct)
        {
            Sent.Enqueue((record.EntityKey, excludedConnections.Order(StringComparer.Ordinal).ToArray()));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_change_is_not_sent_to_the_subscribers_the_visibility_rule_refuses()
    {
        var options = new ChangeHubOptions
        {
            Visibility = (audience, _) => ValueTask.FromResult(audience.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value == "ana" || audience.EntityKey == "public"),
        };
        using var broadcaster = new Recording(new ChangeFeed<TestClient>(), options);
        broadcaster.AddSubscriber("c-ana", As("ana").User);
        broadcaster.AddSubscriber("c-bob", As("bob").User);

        broadcaster.Relay(new EntityChange<TestClient>("7", ChangeType.Update));
        broadcaster.Relay(new EntityChange<TestClient>("public", ChangeType.Update));
        broadcaster.Relay(new EntityChange<TestClient>(string.Empty, ChangeType.None));     // synchronize: names no row

        for (var i = 0; i < 300 && broadcaster.Sent.Count < 3; i++)
            await Task.Delay(10);
        Assert.Equal(["7 -c-bob", "public -", " -"], broadcaster.Sent.Select(s => $"{s.Key} -{string.Join(",", s.Excluded)}"));
    }

    // ---------- Low: a batch with an empty operation, a long trigger name ----------

    [Fact]
    public async Task A_batch_with_an_empty_operation_is_an_invalid_request()
    {
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<TestClient>(Stores, EntityAccessPolicies.AllowAll));
        var dispatcher = new EntityRequestDispatcher(registry, _serializer, Stores);

        var result = await dispatcher.ExecuteBatchAsync([null!], As("ana"));

        Assert.Equal(EntityErrorCode.InvalidRequest, result.ErrorCode);
    }

    [Fact]
    public void A_trigger_name_PostgreSQL_would_cut_is_shortened_with_a_hash()
    {
        var a = PostgresNotifications.TriggerName("queryeables_notify_" + new string('x', 60) + "_orders");
        var b = PostgresNotifications.TriggerName("queryeables_notify_" + new string('x', 60) + "_orderz");

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(a) <= 63);
        Assert.NotEqual(a, b);
        Assert.Equal("queryeables_notify_Clients", PostgresNotifications.TriggerName("queryeables_notify_Clients"));
    }
}

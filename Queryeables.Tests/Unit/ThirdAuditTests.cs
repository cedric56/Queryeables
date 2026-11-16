using System.Collections.Concurrent;
using System.Security.Claims;
using Queryeables.Abstraction;
using Queryeables.PostgreSql;
using Queryeables.Remote;

namespace Queryeables.Tests.Unit;

/// <summary>The fixes of the audit of commit 7ee8e22, each against the way it was found.</summary>
public class ThirdAuditTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();

    private IEntityStoreFactory Stores => new EntityStoreFactory(() => new MemoryStore(_database));

    private static EntityRequestContext As(string user)
        => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test")));

    private async Task<TestClient> SeedAsync(string name, bool active = false)
    {
        var client = new TestClient { Name = name, Active = active };
        await new MemoryStore(_database).InsertAsync(client);
        return client;
    }

    // ---------- 1. A patch is checked against the stored row, not only the client's content ----------

    // ---------- 2. The extended filters are bounded as the plain ones ----------

    // ---------- 3. An aggregate computed in memory is bounded ----------

    // ---------- 7. The offline queue keeps the order of writes on different rows ----------

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
    public void A_trigger_name_PostgreSQL_would_cut_is_shortened_with_a_hash()
    {
        var a = PostgresNotifications.TriggerName("queryeables_notify_" + new string('x', 60) + "_orders");
        var b = PostgresNotifications.TriggerName("queryeables_notify_" + new string('x', 60) + "_orderz");

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(a) <= 63);
        Assert.NotEqual(a, b);
        Assert.Equal("queryeables_notify_Clients", PostgresNotifications.TriggerName("queryeables_notify_Clients"));
    }
}

using System.Collections.Concurrent;
using System.Security.Claims;
using Queryeables.Abstraction;

namespace Queryeables.Tests.Unit;

/// <summary>Change feeds and hubs: what is published, to whom, and how much is queued.</summary>
public class ChangeNotificationTests
{
    // ---------- Client side ----------

    [Fact]
    public void A_receiver_publishes_the_changes_of_the_hub_until_disposed()
    {
        var monitor = new ChangeFeed<TestClient>();
        var received = new List<string>();
        monitor.ChangeReceived += (_, r) => received.Add(r.EntityKey);
        var receiver = new Queryeables.MagicOnion.ChangeHubReceiver<TestClient>(monitor);

        receiver.OnChanged(new Queryeables.MagicOnion.ChangeMessage { EntityKey = "1", ChangeType = ChangeType.Update });
        receiver.Dispose();
        receiver.OnChanged(new Queryeables.MagicOnion.ChangeMessage { EntityKey = "2", ChangeType = ChangeType.Update });

        Assert.Equal(["1"], received);
    }

    // ---------- Server side: ChangeBroadcaster ----------

    private sealed class RecordingBroadcaster(IChangeFeed<TestClient> monitor, ChangeFeedRegistry? registry = null)
        : ChangeBroadcaster<TestClient>(monitor, registry)
    {
        public List<(string Key, string[]? Only)> Sent { get; } = [];
        public TaskCompletionSource FirstSend { get; } = new();

        protected override Task SendAsync(EntityChange<TestClient> record, CancellationToken ct)
        {
            lock (Sent)
                Sent.Add((record.EntityKey, null));
            FirstSend.TrySetResult();
            return Task.CompletedTask;
        }

        protected override Task SendToAsync(EntityChange<TestClient> record, IReadOnlyList<string> recipients, CancellationToken ct)
        {
            lock (Sent)
                Sent.Add((record.EntityKey, recipients.ToArray()));
            FirstSend.TrySetResult();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Broadcaster_sends_database_changes_to_every_subscriber()
    {
        var registry = new ChangeFeedRegistry();
        var monitor = new ChangeFeed<TestClient>("Clients");
        using var broadcaster = new RecordingBroadcaster(monitor, registry);

        registry.Publish(ChangeType.Update, "Clients", 7L);
        await broadcaster.FirstSend.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var (key, only) = Assert.Single(broadcaster.Sent);
        Assert.Equal("7", key);
        Assert.Null(only);                                // everyone, the author of the write included
    }

    [Fact]
    public void Broadcaster_ignores_other_tables()
    {
        var registry = new ChangeFeedRegistry();
        var broadcaster = new RecordingBroadcaster(new ChangeFeed<TestClient>("Clients"), registry);

        registry.Publish(ChangeType.Update, "Commandes", 7L);
        broadcaster.Dispose(); // drains the queue before checking

        Assert.Empty(broadcaster.Sent);
    }

    [Fact]
    public void Broadcaster_delivers_queued_changes_before_disposing()
    {
        var monitor = new ChangeFeed<TestClient>();
        var broadcaster = new RecordingBroadcaster(monitor);

        for (var i = 1; i <= 50; i++)
            broadcaster.Relay(new EntityChange<TestClient>(i, ChangeType.Update));
        broadcaster.Dispose();

        Assert.Equal(Enumerable.Range(1, 50).Select(i => i.ToString()), broadcaster.Sent.Select(s => s.Key));
    }

    [Fact]
    public void Broadcaster_tracks_subscribers()
    {
        using var broadcaster = new RecordingBroadcaster(new ChangeFeed<TestClient>());

        Assert.True(broadcaster.AddSubscriber("a"));
        Assert.False(broadcaster.AddSubscriber("a"));
        Assert.True(broadcaster.RemoveSubscriber("a"));
        Assert.Empty(broadcaster.Subscribers);
    }

    [Fact]
    public void A_monitor_follows_the_table_attribute()
    {
        Assert.Equal("Named_Rows", new ChangeFeed<NamedTable>().Table);
        Assert.Equal(nameof(Member), new ChangeFeed<Member>().Table);
        Assert.Equal("Other", new ChangeFeed<NamedTable>("Other").Table);
    }

    // ---------- Hubs: who is admitted, how much is queued ----------

    private sealed class Recorder(ChangeHubOptions? options = null)
        : ChangeBroadcaster<TestClient>(new ChangeFeed<TestClient>(), options: options)
    {
        public List<EntityChange<TestClient>> Sent { get; } = [];

        public TaskCompletionSource? Gate { get; set; }

        protected override async Task SendAsync(EntityChange<TestClient> record, CancellationToken ct)
        {
            if (Gate is { } gate)
                await gate.Task.WaitAsync(ct);
            lock (Sent)
                Sent.Add(record);
        }

        protected override Task SendToAsync(EntityChange<TestClient> record, IReadOnlyList<string> recipients, CancellationToken ct)
            => SendAsync(record, ct);
    }

    [Fact]
    public void A_hub_admits_anonymous_connections_only_when_told_to()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var known = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ana")], "test"));
        using var closed = new Recorder();
        using var open = new Recorder(new ChangeHubOptions { AllowAnonymous = true });

        Assert.False(closed.Admits(null));
        Assert.False(closed.Admits(anonymous));
        Assert.True(closed.Admits(known));
        Assert.True(open.Admits(anonymous));
    }

    [Fact]
    public async Task A_full_queue_turns_into_one_synchronization_instead_of_growing()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broadcaster = new Recorder(new ChangeHubOptions { QueueCapacity = 2 }) { Gate = gate };

        for (var i = 0; i < 50; i++)
            broadcaster.Relay(new EntityChange<TestClient>(i.ToString(), ChangeType.Update));
        await Task.Delay(50);
        gate.SetResult();
        broadcaster.Dispose();

        Assert.True(broadcaster.Sent.Count <= 4, $"{broadcaster.Sent.Count} sent for a queue of 2");
        Assert.Contains(broadcaster.Sent, s => s.ChangeType == ChangeType.None && s.EntityKey.Length == 0);
    }

    // ---------- Visibility: a change reaches only the users who may see its row ----------

    private static ClaimsPrincipal User(string id)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    private sealed class Recording(IChangeFeed<TestClient> monitor, ChangeHubOptions options) : ChangeBroadcaster<TestClient>(monitor, options: options)
    {
        public ConcurrentQueue<string> Sent { get; } = new();

        protected override Task SendAsync(EntityChange<TestClient> record, CancellationToken ct)
        {
            Sent.Enqueue($"{record.EntityKey}: all");
            return Task.CompletedTask;
        }

        protected override Task SendToAsync(EntityChange<TestClient> record, IReadOnlyList<string> recipients, CancellationToken ct)
        {
            Sent.Enqueue($"{record.EntityKey}: {string.Join(",", recipients.Order(StringComparer.Ordinal))}");
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
        broadcaster.AddSubscriber("c-ana", User("ana"));
        broadcaster.AddSubscriber("c-bob", User("bob"));

        broadcaster.Relay(new EntityChange<TestClient>("7", ChangeType.Update));
        broadcaster.Relay(new EntityChange<TestClient>("public", ChangeType.Update));
        broadcaster.Relay(new EntityChange<TestClient>(string.Empty, ChangeType.None));     // synchronize: names no row

        for (var i = 0; i < 300 && broadcaster.Sent.Count < 3; i++)
            await Task.Delay(10);
        Assert.Equal(["7: c-ana", "public: c-ana,c-bob", ": all"], broadcaster.Sent.ToArray());
    }

    private sealed class Targeted(IChangeFeed<TestClient> monitor, ChangeHubOptions options) : ChangeBroadcaster<TestClient>(monitor, options: options)
    {
        public ConcurrentQueue<string> Sent { get; } = new();

        protected override Task SendAsync(EntityChange<TestClient> record, CancellationToken ct)
        {
            Sent.Enqueue($"{record.EntityKey}: all");
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
        broadcaster.AddSubscriber("c-ana", User("ana"));
        broadcaster.AddSubscriber("c-bob", User("bob"));

        broadcaster.Relay(new EntityChange<TestClient>("7", ChangeType.Update));
        broadcaster.Relay(new EntityChange<TestClient>(string.Empty, ChangeType.None));

        for (var i = 0; i < 300 && broadcaster.Sent.Count < 2; i++)
            await Task.Delay(10);
        Assert.Equal(["7: only c-ana", ": all"], broadcaster.Sent.ToArray());
    }
}

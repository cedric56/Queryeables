using Queryeables.Abstraction;

namespace Queryeables.Tests.Unit;

public class ChangePropagationTests
{
    // ---------- Client side: ChangeRelay ----------

    [Fact]
    public void Relay_publishes_changes_from_other_clients()
    {
        var monitor = new ChangeFeed<TestClient>();
        var received = new List<EntityChange<TestClient>>();
        monitor.ChangeReceived += (_, r) => received.Add(r);
        using var relay = new ChangeRelay<TestClient>(monitor, () => "moi");

        relay.Receive(new EntityChange<TestClient>(1L, ChangeType.Update, ["autre"]));

        Assert.Single(received);
    }

    [Fact]
    public void Relay_ignores_its_own_changes()
    {
        var monitor = new ChangeFeed<TestClient>();
        var received = 0;
        monitor.ChangeReceived += (_, _) => received++;
        using var relay = new ChangeRelay<TestClient>(monitor, () => "moi");

        relay.Receive(new EntityChange<TestClient>(1L, ChangeType.Update, ["moi"]));

        Assert.Equal(0, received);
    }

    [Fact]
    public async Task Relay_sends_local_changes_with_its_id()
    {
        var monitor = new ChangeFeed<TestClient>();
        var sent = new TaskCompletionSource<EntityChange<TestClient>>();
        using var relay = new ChangeRelay<TestClient>(monitor, () => "moi", r => { sent.TrySetResult(r); return Task.CompletedTask; });

        monitor.Publish(new EntityChange<TestClient>(1L, ChangeType.Insert));

        var record = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(record.Contains("moi"));
    }

    [Fact]
    public void Relay_never_sends_back_a_change_received_from_the_server()
    {
        var monitor = new ChangeFeed<TestClient>();
        var sent = 0;
        using var relay = new ChangeRelay<TestClient>(monitor, () => "moi", _ => { sent++; return Task.CompletedTask; });

        relay.Receive(new EntityChange<TestClient>(1L, ChangeType.Update, ["autre"]));

        Assert.Equal(0, sent);
    }

    [Fact]
    public void Relay_sends_nothing_while_disconnected()
    {
        var monitor = new ChangeFeed<TestClient>();
        var sent = 0;
        using var relay = new ChangeRelay<TestClient>(monitor, () => null, _ => { sent++; return Task.CompletedTask; });

        monitor.Publish(new EntityChange<TestClient>(1L, ChangeType.Insert));

        Assert.Equal(0, sent);
    }

    // ---------- Server side: ChangeBroadcaster ----------

    private sealed class RecordingBroadcaster(IChangeFeed<TestClient> monitor, ChangeFeedRegistry? registry = null)
        : ChangeBroadcaster<TestClient>(monitor, registry)
    {
        public List<(string Key, string[] Excluded)> Sent { get; } = [];
        public TaskCompletionSource FirstSend { get; } = new();

        protected override Task SendAsync(EntityChange<TestClient> record, IReadOnlyList<string> excludedConnections, CancellationToken ct)
        {
            lock (Sent)
                Sent.Add((record.EntityKey, excludedConnections.ToArray()));
            FirstSend.TrySetResult();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Broadcaster_sends_database_changes_excluding_their_author()
    {
        var registry = new ChangeFeedRegistry();
        var monitor = new ChangeFeed<TestClient>("Clients");
        using var broadcaster = new RecordingBroadcaster(monitor, registry);

        registry.Publish(ChangeType.Update, "Clients", 7L, connectionId: "auteur");
        await broadcaster.FirstSend.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var (key, excluded) = Assert.Single(broadcaster.Sent);
        Assert.Equal("7", key);
        Assert.Equal(new[] { "auteur" }, excluded);
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
}

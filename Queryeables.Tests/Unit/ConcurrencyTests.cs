using Queryeables.Abstraction;
using System.Collections.Concurrent;

namespace Queryeables.Tests.Unit;

/// <summary>Notifications arriving from many threads at once, and clients that reconnect.</summary>
public class ConcurrencyTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        if (!condition())
            throw new TimeoutException(because);
    }

    [Fact]
    public async Task A_cache_converges_under_parallel_notifications()
    {
        var rows = Enumerable.Range(1, 200).Select(i => new TestClient { Id = i, Name = $"v0-{i}" }).ToList();
        var monitor = new DbMonitor<TestClient>();
        using var cache = new EntityCache<TestClient, long>(() => new InMemoryEntityStore(rows), c => c.Id, monitor,
            DbRecordKey.Parse<long>);
        await cache.LoadAsync();
        var failures = new ConcurrentBag<Exception>();
        cache.RefreshFailed += (_, ex) => failures.Add(ex);

        // Every row changes, then each is notified several times, from many threads at once.
        foreach (var row in rows)
            row.Name = $"v1-{row.Id}";
        Parallel.For(0, rows.Count * 3, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => monitor.Publish(new DbRecord<TestClient>(rows[i % rows.Count].Id, ChangeType.Update)));

        await WaitUntilAsync(() => cache.Entities.All(c => c.Name.StartsWith("v1-", StringComparison.Ordinal)),
            "the cache did not converge");
        Assert.Empty(failures);
        Assert.Equal(rows.Count, cache.Entities.Count);
    }

    [Fact]
    public async Task A_broadcaster_keeps_the_order_of_each_source_under_concurrency()
    {
        var monitor = new DbMonitor<TestClient>();
        using var broadcaster = new CollectingBroadcaster(monitor);

        const int Sources = 8, PerSource = 250;
        Parallel.For(0, Sources, source =>
        {
            for (var n = 0; n < PerSource; n++)
                broadcaster.Relay(new DbRecord<TestClient>($"{source}:{n}", ChangeType.Update));
        });

        await WaitUntilAsync(() => broadcaster.Sent.Count == Sources * PerSource, "some changes were not broadcast");
        foreach (var group in broadcaster.Sent.ToArray().GroupBy(key => key.Split(':')[0]))
        {
            var numbers = group.Select(key => int.Parse(key.Split(':')[1])).ToList();
            Assert.Equal(Enumerable.Range(0, PerSource), numbers);
        }
    }

    [Fact]
    public void A_reconnected_client_ignores_its_own_changes_under_its_new_id()
    {
        var monitor = new DbMonitor<TestClient>();
        var received = new List<string>();
        monitor.RecordChanged += (_, record) => received.Add(record.EntityKey);
        var connectionId = "first";
        using var relay = new ChangeRelay<TestClient>(monitor, () => connectionId);

        relay.Receive(new DbRecord<TestClient>("1", ChangeType.Update, ["first"]));    // its own change: ignored
        connectionId = "second";                                                       // reconnected, new id
        relay.Receive(new DbRecord<TestClient>("2", ChangeType.Update, ["second"]));   // its own change: ignored
        relay.Receive(new DbRecord<TestClient>("3", ChangeType.Update, ["first"]));    // made under the old id: received

        Assert.Equal(new[] { "3" }, received);
    }

    [Fact]
    public async Task Disposing_a_broadcaster_does_not_wait_for_a_stuck_client()
    {
        var monitor = new DbMonitor<TestClient>();
        var broadcaster = new StuckBroadcaster(monitor);
        broadcaster.Relay(new DbRecord<TestClient>("1", ChangeType.Update));
        await broadcaster.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        broadcaster.Dispose();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {watch.Elapsed}");
    }

    private sealed class CollectingBroadcaster(IDbMonitor<TestClient> monitor) : ChangeBroadcaster<TestClient>(monitor)
    {
        public ConcurrentQueue<string> Sent { get; } = new();

        protected override Task SendAsync(DbRecord<TestClient> record, IReadOnlyList<string> excludedConnections, CancellationToken ct)
        {
            Sent.Enqueue(record.EntityKey);
            return Task.CompletedTask;
        }
    }

    /// <summary>A client that never acknowledges: the send only ends when it is cancelled.</summary>
    private sealed class StuckBroadcaster(IDbMonitor<TestClient> monitor) : ChangeBroadcaster<TestClient>(monitor)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override TimeSpan DrainTimeout => TimeSpan.FromMilliseconds(200);

        protected override async Task SendAsync(DbRecord<TestClient> record, IReadOnlyList<string> excludedConnections, CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }
}

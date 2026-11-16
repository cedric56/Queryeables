using Queryeables.Abstraction;
using Queryeables.Remote;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Queryeables.Tests.Unit;

/// <summary>
/// Traces and metrics, as OpenTelemetry would read them. Other tests may run at the same time and record too: each
/// assertion looks for its own measurement, never for an exact count.
/// </summary>
public sealed class DiagnosticsTests : IDisposable
{
    private readonly MeterListener _meters = new();
    private readonly ActivityListener _activities;
    private readonly ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = new();
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly MemoryDatabase _database = new();

    public DiagnosticsTests()
    {
        _meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == QueryeablesDiagnostics.Name)
                listener.EnableMeasurementEvents(instrument);
        };
        _meters.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
        _meters.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
        _meters.Start();

        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == QueryeablesDiagnostics.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _stopped.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(_activities);
    }

    public void Dispose()
    {
        _meters.Dispose();
        _activities.Dispose();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>();
        foreach (var tag in tags)
            copy[tag.Key] = tag.Value;
        _measurements.Enqueue((instrument.Name, value, copy));
    }

    private bool Measured(string instrument, params (string Tag, string Value)[] tags)
        => _measurements.Any(m => m.Instrument == instrument && tags.All(t => Equals(m.Tags.GetValueOrDefault(t.Tag), t.Value)));

    private static readonly string Client = EntityName.Of<TestClient>();

    [Fact]
    public void Published_changes_are_counted()
    {
        var registry = new ChangeFeedRegistry();

        registry.Publish(ChangeType.Update, "DiagnosticsTable", 42L);

        Assert.True(Measured("queryeables.changes.published", ("queryeables.table", "DiagnosticsTable"), ("queryeables.change", "Update")));
    }

    [Fact]
    public async Task Cache_loads_are_counted_and_traced()
    {
        using var cache = new EntityCache<TestClient, long>(() => new InMemoryEntityStore([]), c => c.Id);

        await cache.LoadAsync();

        Assert.True(Measured("queryeables.cache.refreshes", ("queryeables.type", nameof(TestClient)), ("queryeables.kind", "load")));
        Assert.Contains(_stopped, a => a.OperationName == "queryeables.cache" && Equals(a.GetTagItem("queryeables.type"), nameof(TestClient)));
    }

    [Fact]
    public void Error_kinds_become_short_outcome_tags()
    {
        Assert.Equal("ok", QueryeablesDiagnostics.Outcome(EntityErrorCode.None));
        Assert.Equal("not_found", QueryeablesDiagnostics.Outcome(EntityErrorCode.NotFound));
        Assert.Equal("conflict", QueryeablesDiagnostics.Outcome(EntityErrorCode.Conflict));
    }
}

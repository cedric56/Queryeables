using Queryeables.Abstraction;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Queryeables.Remote;

/// <summary>The instruments of remote clients, servers and the offline mode (see <see cref="QueryeablesDiagnostics"/>).</summary>
internal static class RemoteDiagnostics
{
    private static readonly Histogram<double> ClientDuration = QueryeablesDiagnostics.Meter.CreateHistogram<double>(
        "queryeables.remote.duration", "ms", "Calls of a remote client.");

    private static readonly Histogram<double> ServerDuration = QueryeablesDiagnostics.Meter.CreateHistogram<double>(
        "queryeables.server.duration", "ms", "Requests handled by a server.");

    internal static readonly Counter<long> OfflineSent = QueryeablesDiagnostics.Meter.CreateCounter<long>(
        "queryeables.offline.sent", "{write}", "Offline writes sent and accepted by the server.");

    internal static readonly Counter<long> OfflineConflicts = QueryeablesDiagnostics.Meter.CreateCounter<long>(
        "queryeables.offline.conflicts", "{write}", "Offline writes refused by the server.");

    /// <summary>A call of a remote client: timed, traced, its outcome tagged.</summary>
    public static Task<TransportResult> ClientAsync(string operation, string type, Func<Task<TransportResult>> call)
        => TrackAsync("queryeables.remote", ActivityKind.Client, ClientDuration, operation, type, call);

    /// <summary>A request handled by a server.</summary>
    public static Task<TransportResult> ServerAsync(string operation, string type, Func<Task<TransportResult>> call)
        => TrackAsync("queryeables.server", ActivityKind.Server, ServerDuration, operation, type, call);

    private static async Task<TransportResult> TrackAsync(string name, ActivityKind kind, Histogram<double> duration, string operation, string type,
        Func<Task<TransportResult>> call)
    {
        // Nobody listens: no clock, no allocation.
        if (!duration.Enabled && !QueryeablesDiagnostics.ActivitySource.HasListeners())
            return await call();

        using var activity = QueryeablesDiagnostics.ActivitySource.StartActivity(name, kind);
        activity?.SetTag(QueryeablesDiagnostics.OperationTag, operation);
        activity?.SetTag(QueryeablesDiagnostics.TypeTag, type);
        var started = Stopwatch.GetTimestamp();
        var outcome = "unreachable";
        try
        {
            var result = await call();
            outcome = QueryeablesDiagnostics.Outcome(result.Successful ? EntityErrorCode.None : result.ErrorCode);
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            activity?.SetTag(QueryeablesDiagnostics.OutcomeTag, outcome);
            if (outcome is not ("ok" or "cancelled"))
                activity?.SetStatus(ActivityStatusCode.Error, outcome);
            duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>(QueryeablesDiagnostics.OperationTag, operation),
                new KeyValuePair<string, object?>(QueryeablesDiagnostics.TypeTag, type),
                new KeyValuePair<string, object?>(QueryeablesDiagnostics.OutcomeTag, outcome));
        }
    }
}

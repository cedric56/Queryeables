using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Queryeables.Abstraction;

/// <summary>
/// Traces and metrics, through the .NET APIs that OpenTelemetry, <c>dotnet-counters</c> and Application Insights read:
/// listen to the <see cref="ActivitySource"/> and the <see cref="Meter"/> named <see cref="Name"/>. Nothing is recorded,
/// and nothing costs more than a check, while nobody listens.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term>queryeables.remote.duration</term><description>Calls of a remote client, in ms; activity <c>queryeables.remote</c>.</description></item>
/// <item><term>queryeables.server.duration</term><description>Requests handled by a server, in ms; activity <c>queryeables.server</c>.</description></item>
/// <item><term>queryeables.changes.published</term><description>Row changes published to the change feeds.</description></item>
/// <item><term>queryeables.cache.refreshes</term><description>Cache loads, synchronizations and row refreshes; activity <c>queryeables.cache</c>.</description></item>
/// <item><term>queryeables.offline.sent</term><description>Offline writes sent and accepted; activity <c>queryeables.offline.sync</c>.</description></item>
/// <item><term>queryeables.offline.conflicts</term><description>Offline writes refused by the server.</description></item>
/// </list>
/// Tags: <c>queryeables.type</c> (entity type), <c>queryeables.operation</c>, <c>queryeables.outcome</c> (<c>ok</c>, an
/// error kind such as <c>conflict</c>, or <c>unreachable</c>), <c>queryeables.table</c>, <c>queryeables.change</c>,
/// <c>queryeables.kind</c>.
/// </remarks>
/// <example>
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(QueryeablesDiagnostics.Name))
///     .WithMetrics(m => m.AddMeter(QueryeablesDiagnostics.Name));
/// </code>
/// </example>
public static class QueryeablesDiagnostics
{
    public const string Name = "Queryeables";

    public const string TypeTag = "queryeables.type";
    public const string OperationTag = "queryeables.operation";
    public const string OutcomeTag = "queryeables.outcome";
    public const string TableTag = "queryeables.table";
    public const string ChangeTag = "queryeables.change";
    public const string KindTag = "queryeables.kind";

    private static readonly string? Version = typeof(QueryeablesDiagnostics).Assembly.GetName().Version?.ToString();

    public static ActivitySource ActivitySource { get; } = new(Name, Version);

    public static Meter Meter { get; } = new(Name, Version);

    internal static readonly Counter<long> ChangesPublished =
        Meter.CreateCounter<long>("queryeables.changes.published", "{change}", "Row changes published to the change feeds.");

    internal static readonly Counter<long> CacheRefreshes =
        Meter.CreateCounter<long>("queryeables.cache.refreshes", "{refresh}", "Cache loads, synchronizations and row refreshes.");

    /// <summary>The outcome tag of an error kind: <c>ok</c>, <c>conflict</c>, <c>not_found</c>...</summary>
    public static string Outcome(EntityErrorCode code) => code switch
    {
        EntityErrorCode.None => "ok",
        EntityErrorCode.NotFound => "not_found",
        EntityErrorCode.UnknownType => "unknown_type",
        EntityErrorCode.NotSupported => "not_supported",
        EntityErrorCode.InvalidRequest => "invalid_request",
        _ => code.ToString().ToLowerInvariant(),
    };

    /// <summary>Counts a cache refresh, in an activity when someone listens.</summary>
    internal static Activity? StartCacheRefresh(Type entity, string kind)
    {
        if (CacheRefreshes.Enabled)
            CacheRefreshes.Add(1, new KeyValuePair<string, object?>(TypeTag, entity.Name), new KeyValuePair<string, object?>(KindTag, kind));
        var activity = ActivitySource.StartActivity("queryeables.cache");
        activity?.SetTag(TypeTag, entity.Name);
        activity?.SetTag(KindTag, kind);
        return activity;
    }
}

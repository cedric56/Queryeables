using Queryeables.Abstraction;
using System.Diagnostics.Metrics;

using Queryeables.Remote;

namespace Queryeables.Offline;

/// <summary>
/// The instruments of the offline mode, on the meter of <see cref="QueryeablesDiagnostics"/>:
/// <c>queryeables.offline.sent</c> (offline writes sent and accepted; activity <c>queryeables.offline.sync</c>) and
/// <c>queryeables.offline.conflicts</c> (offline writes refused by the server).
/// </summary>
internal static class OfflineDiagnostics
{
    internal static readonly Counter<long> OfflineSent = QueryeablesDiagnostics.Meter.CreateCounter<long>(
        "queryeables.offline.sent", "{write}", "Offline writes sent and accepted by the server.");

    internal static readonly Counter<long> OfflineConflicts = QueryeablesDiagnostics.Meter.CreateCounter<long>(
        "queryeables.offline.conflicts", "{write}", "Offline writes refused by the server.");
}

using Queryeables.Abstraction;

namespace Queryeables.Remote.Offline;

/// <summary>Where the stores read and write.</summary>
public enum ConnectionMode
{
    /// <summary>On the server, through the remote stores.</summary>
    Online,

    /// <summary>On the local copy; writes wait in a queue until the user synchronizes.</summary>
    Offline,
}

/// <summary>Why the status changed: what the application may tell the user.</summary>
public enum OfflineEvent
{
    /// <summary>A call to the server failed for lack of network: offer to work offline.</summary>
    ServerUnreachable,

    /// <summary>The server answers again. Offline, offer to synchronize: <see cref="OfflineStatus.IsDesynchronized"/> tells whether there is anything to exchange.</summary>
    ServerAvailable,

    /// <summary>A write was queued while offline.</summary>
    ChangesPending,

    /// <summary>The mode changed, after the user's choice.</summary>
    ModeChanged,

    /// <summary>The local copy was refreshed from the server.</summary>
    SnapshotRefreshed,

    /// <summary>A synchronization ended: what was sent, and the conflicts to resolve.</summary>
    Synchronized,
}

/// <summary>The state the user is shown.</summary>
/// <param name="Mode">Online or offline.</param>
/// <param name="ServerReachable">Whether the server answered the last attempt; null before any.</param>
/// <param name="PendingChanges">Writes made offline, not sent yet.</param>
/// <param name="Conflicts">Writes the server refused during a synchronization, waiting for the user's decision.</param>
/// <param name="SnapshotTakenAt">When the local copy was taken from the server; null without a copy.</param>
/// <param name="ServerChanges">
/// Rows changed on the server since the local copy, when the server is reachable and the types have [LastModified];
/// null when unknown.
/// </param>
public sealed record OfflineStatus(
    ConnectionMode Mode,
    bool? ServerReachable,
    int PendingChanges,
    int Conflicts,
    DateTimeOffset? SnapshotTakenAt,
    int? ServerChanges)
{
    /// <summary>The local data and the server differ: local writes to send, conflicts, or server changes not received.</summary>
    public bool IsDesynchronized => PendingChanges > 0 || Conflicts > 0 || ServerChanges > 0;
}

public sealed class OfflineStatusChangedEventArgs(OfflineEvent reason, OfflineStatus status) : EventArgs
{
    public OfflineEvent Reason { get; } = reason;

    public OfflineStatus Status { get; } = status;
}

/// <summary>A write made offline that the server refused during a synchronization.</summary>
/// <param name="Id">Identifies the conflict.</param>
/// <param name="Type">The entity type, as named on the wire.</param>
/// <param name="Kind">Insert, update or delete.</param>
/// <param name="Key">The key of the entity, formatted (see <see cref="EntityKey"/>).</param>
/// <param name="Payload">The entity as it was written offline (see <see cref="OfflineMode.ReadConflict{TEntity}"/>).</param>
/// <param name="ErrorCode">Why: Conflict (someone else changed it), NotFound (deleted on the server), Validation, Forbidden...</param>
/// <param name="Message">The server's message.</param>
/// <param name="At">When the write was made offline.</param>
public sealed record OfflineConflict(
    Guid Id,
    string Type,
    BatchOperationKind Kind,
    string Key,
    byte[] Payload,
    EntityErrorCode ErrorCode,
    string Message,
    DateTime At);

/// <summary>The outcome of a synchronization.</summary>
/// <param name="Completed">False when the server could not be reached: nothing more was sent, the mode did not change.</param>
/// <param name="Sent">Writes the server accepted.</param>
/// <param name="Conflicts">Writes the server refused, to resolve with <see cref="OfflineMode.KeepMineAsync"/> or <see cref="OfflineMode.KeepServerAsync"/>.</param>
/// <param name="Error">Why the synchronization stopped, when it did.</param>
public sealed record SyncReport(bool Completed, int Sent, IReadOnlyList<OfflineConflict> Conflicts, string? Error);

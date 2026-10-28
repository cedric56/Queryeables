using System.Collections.Concurrent;

namespace Queryeables.Abstraction;

/// <summary>
/// Set of active monitors. The change source (SQLite hook, trigger...) only calls
/// <see cref="Publish(ChangeType, string, string, string?)"/>, and each monitor filters the tables it cares about.
/// </summary>
/// <example>
/// <code>
/// // In the SQLite update hook, for each modified row:
/// registry.Publish(ChangeType.Update, table, rowId);
/// </code>
/// </example>
public sealed class DbMonitorRegistry
{
    private readonly ConcurrentDictionary<IDbMonitor, byte> _monitors = new();

    public void Add(IDbMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitors.TryAdd(monitor, 0);
    }

    public bool Remove(IDbMonitor monitor) => _monitors.TryRemove(monitor, out _);

    public IReadOnlyList<IDbMonitor> GetAll() => _monitors.Keys.ToArray();

    /// <param name="connectionId">
    /// Client that initiated the change. Defaults to <see cref="ChangeContext.ConnectionId"/>,
    /// set by the server during the write.
    /// </param>
    public void Publish(ChangeType type, string table, string entityKey, string? connectionId = null)
    {
        connectionId ??= ChangeContext.ConnectionId;
        foreach (var monitor in _monitors.Keys)
            monitor.Publish(type, table, entityKey, connectionId);
    }

    /// <summary>Overload for a numeric key, for example the SQLite rowid.</summary>
    public void Publish(ChangeType type, string table, long rowId, string? connectionId = null)
        => Publish(type, table, DbRecordKey.Format(rowId), connectionId);

    /// <summary>Overload for any key (Guid, string, tuple for a composite key...).</summary>
    public void Publish<TKey>(ChangeType type, string table, TKey key, string? connectionId = null) where TKey : notnull
        => Publish(type, table, DbRecordKey.Format(key), connectionId);
}

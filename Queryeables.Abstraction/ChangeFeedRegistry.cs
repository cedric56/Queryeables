using System.Collections.Concurrent;

namespace Queryeables.Abstraction;

/// <summary>
/// Set of active change feeds. The change source (SQLite hook, trigger...) only calls
/// <see cref="Publish(ChangeType, string, string, string?)"/>, and each change feed filters the tables it cares about.
/// </summary>
/// <example>
/// <code>
/// // In the SQLite update hook, for each modified row:
/// registry.Publish(ChangeType.Update, table, rowId);
/// </code>
/// </example>
public sealed class ChangeFeedRegistry
{
    private readonly ConcurrentDictionary<IChangeFeed, byte> _monitors = new();

    public void Add(IChangeFeed monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitors.TryAdd(monitor, 0);
    }

    public bool Remove(IChangeFeed monitor) => _monitors.TryRemove(monitor, out _);

    public IReadOnlyList<IChangeFeed> GetAll() => _monitors.Keys.ToArray();

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

    /// <summary>Tells every change feed that changes may have been missed (see <see cref="IChangeFeed.PublishResync"/>).</summary>
    public void PublishResync()
    {
        foreach (var monitor in _monitors.Keys)
            monitor.PublishResync();
    }

    /// <summary>Overload for a numeric key, for example the SQLite rowid.</summary>
    public void Publish(ChangeType type, string table, long rowId, string? connectionId = null)
        => Publish(type, table, EntityKey.Format(rowId), connectionId);

    /// <summary>Overload for any key (Guid, string, tuple for a composite key...).</summary>
    public void Publish<TKey>(ChangeType type, string table, TKey key, string? connectionId = null) where TKey : notnull
        => Publish(type, table, EntityKey.Format(key), connectionId);
}

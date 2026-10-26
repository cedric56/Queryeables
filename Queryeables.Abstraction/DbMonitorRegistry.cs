using System.Collections.Concurrent;

namespace Queryeables.Abstraction;

/// <summary>
/// Ensemble des moniteurs actifs. La source de changements (hook SQLite, trigger...) n'appelle que
/// <see cref="Publish"/>, et chaque moniteur filtre les tables qui le concernent.
/// </summary>
/// <example>
/// <code>
/// // Dans le hook de mise à jour SQLite, pour chaque ligne modifiée :
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
    /// Client à l'origine du changement. Par défaut, celui de <see cref="ChangeContext.ConnectionId"/>,
    /// renseigné par le serveur pendant l'écriture.
    /// </param>
    public void Publish(ChangeType type, string table, string entityKey, string? connectionId = null)
    {
        connectionId ??= ChangeContext.ConnectionId;
        foreach (var monitor in _monitors.Keys)
            monitor.Publish(type, table, entityKey, connectionId);
    }

    /// <summary>Variante pour une clé numérique, par exemple le rowid SQLite.</summary>
    public void Publish(ChangeType type, string table, long rowId, string? connectionId = null)
        => Publish(type, table, RecordKey.Format(rowId), connectionId);

    /// <summary>Variante pour une clé quelconque (Guid, string, tuple pour une clé composite...).</summary>
    public void Publish<TKey>(ChangeType type, string table, TKey key, string? connectionId = null) where TKey : notnull
        => Publish(type, table, RecordKey.Format(key), connectionId);
}

namespace Queryeables.Abstraction;

/// <summary>
/// Change monitor without an entity type: this is what the notification source sees
/// (SQLite update hook, trigger...), through <see cref="DbMonitorRegistry"/>.
/// </summary>
public interface IDbMonitor
{
    /// <summary>
    /// Reports that a row changed. The monitor ignores the tables it does not care about.
    /// </summary>
    /// <param name="entityKey">Row key, in the <see cref="RecordKey.Format"/> format.</param>
    void Publish(ChangeType type, string table, string entityKey, string? connectionId);
}

/// <summary>Monitor of one entity type: its subscribers (for example <see cref="EntityCache{TEntity, TKey}"/>) react to changes.</summary>
public interface IDbMonitor<TEntity> : IDbMonitor where TEntity : class
{
    event EventHandler<Record<TEntity>>? RecordChanged;

    /// <summary>Raises <see cref="RecordChanged"/> with a change received as is (for example from a hub).</summary>
    void Publish(Record<TEntity> record);
}

/// <summary>
/// Standard implementation. Server side, the change source calls
/// <see cref="Publish(ChangeType, string, string, string?)"/>; client side, the hub receiver calls
/// <see cref="Publish(Record{TEntity})"/>.
/// </summary>
public sealed class DbMonitor<TEntity> : IDbMonitor<TEntity> where TEntity : class
{
    /// <param name="table">Name of the monitored table. Defaults to the entity type name.</param>
    public DbMonitor(string? table = null) => Table = table ?? typeof(TEntity).Name;

    /// <summary>Name of the monitored table.</summary>
    public string Table { get; }

    public event EventHandler<Record<TEntity>>? RecordChanged;

    public void Publish(Record<TEntity> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        RecordChanged?.Invoke(this, record);
    }

    public void Publish(ChangeType type, string table, string entityKey, string? connectionId)
    {
        if (!string.Equals(table, Table, StringComparison.OrdinalIgnoreCase))
            return;

        List<string> connections = string.IsNullOrWhiteSpace(connectionId) ? [] : [connectionId];
        Publish(new Record<TEntity>(entityKey, type, connections));
    }
}

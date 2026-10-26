namespace Queryeables.Abstraction;

/// <summary>
/// Moniteur de changements, sans type d'entité : c'est ce que voit la source de notifications
/// (hook de mise à jour SQLite, trigger...), via <see cref="DbMonitorRegistry"/>.
/// </summary>
public interface IDbMonitor
{
    /// <summary>
    /// Signale qu'une ligne a changé. Le moniteur ignore les tables qui ne le concernent pas.
    /// </summary>
    /// <param name="entityKey">Clé de la ligne, au format de <see cref="RecordKey.Format"/>.</param>
    void Publish(ChangeType type, string table, string entityKey, string? connectionId);
}

/// <summary>Moniteur d'un type d'entité : ses abonnés (par exemple <see cref="EntityCache{TEntity, TKey}"/>) réagissent aux changements.</summary>
public interface IDbMonitor<TEntity> : IDbMonitor where TEntity : class
{
    event EventHandler<Record<TEntity>>? RecordChanged;
}

/// <summary>
/// Implémentation standard. Côté serveur, la source de changements appelle
/// <see cref="Publish(ChangeType, string, string, string?)"/> ; côté client, le récepteur du hub appelle
/// <see cref="Publish(Record{TEntity})"/>.
/// </summary>
public sealed class DbMonitor<TEntity> : IDbMonitor<TEntity> where TEntity : class
{
    /// <param name="table">Nom de la table surveillée. Par défaut, le nom du type d'entité.</param>
    public DbMonitor(string? table = null) => Table = table ?? typeof(TEntity).Name;

    /// <summary>Nom de la table surveillée.</summary>
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

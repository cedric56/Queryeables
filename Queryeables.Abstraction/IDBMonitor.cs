namespace Queryeables.Abstraction;

/// <summary>Marqueur non générique, pratique pour enregistrer ou lister les moniteurs.</summary>
public interface IDbMonitor
{
}

/// <summary>
/// Signale les changements de lignes d'une table (insertion, mise à jour, suppression).
/// Le moniteur ne fait qu'annoncer : ce sont les abonnés, comme <see cref="EntityCache{TEntity, TKey}"/>, qui réagissent.
/// </summary>
public interface IDbMonitor<TEntity> : IDbMonitor where TEntity : class
{
    event EventHandler<Record<TEntity>>? RecordChanged;
}

/// <summary>
/// Implémentation simple : la source de notifications (hook SQLite, MagicOnion, SignalR...)
/// appelle <see cref="Publish"/>, et le moniteur relaie aux abonnés.
/// </summary>
public sealed class DbMonitor<TEntity> : IDbMonitor<TEntity> where TEntity : class
{
    public event EventHandler<Record<TEntity>>? RecordChanged;

    public void Publish(Record<TEntity> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        RecordChanged?.Invoke(this, record);
    }
}

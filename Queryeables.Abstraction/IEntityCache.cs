namespace Queryeables.Abstraction;

/// <summary>
/// In-memory cache of one entity type, fed by an <see cref="IEntityStore"/>.
/// Framework-independent, like <see cref="IEntityStore"/>.
/// </summary>
/// <typeparam name="TEntity">Cached entity type.</typeparam>
/// <typeparam name="TKey">
/// Primary key type: <c>int</c>, <c>long</c>, <c>Guid</c>, <c>string</c>…
/// For a composite key, use a tuple, for example <c>(int OrderId, int Line)</c>.
/// </typeparam>
public interface IEntityCache<TEntity, TKey>
    where TEntity : class
    where TKey : notnull
{
    /// <summary>Entities currently in the cache.</summary>
    IReadOnlyCollection<TEntity> Entities { get; }

    /// <summary>Raised after a load or a change of the cache.</summary>
    event EventHandler? EntitiesChanged;

    /// <summary>(Re)loads the cache from the data source.</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>Returns the entity matching the key, or null when it is not in the cache.</summary>
    TEntity? Find(TKey key);

    /// <summary>Adds the entity to the cache, or replaces the one with the same key.</summary>
    void Track(TEntity entity);

    /// <summary>
    /// Catches up after a disconnection: reads only the rows changed since the last synchronization when the type has
    /// [LastModified], reloads everything otherwise.
    /// </summary>
    Task SyncAsync(CancellationToken ct = default) => LoadAsync(ct);
}
namespace Queryeables.Abstraction;

/// <summary>
/// Cache en mémoire d'un type d'entité, alimenté par un <see cref="IEntityStore"/>.
/// Indépendant du framework, comme <see cref="IEntityStore"/>.
/// </summary>
/// <typeparam name="TEntity">Type d'entité mis en cache.</typeparam>
/// <typeparam name="TKey">
/// Type de la clé primaire : <c>int</c>, <c>long</c>, <c>Guid</c>, <c>string</c>…
/// Pour une clé composite, utilisez un tuple, par exemple <c>(int CommandeId, int Ligne)</c>.
/// </typeparam>
public interface IEntityCache<TEntity, TKey>
    where TEntity : class
    where TKey : notnull
{
    /// <summary>Entités actuellement en cache.</summary>
    IReadOnlyCollection<TEntity> Entities { get; }

    /// <summary>Levé après un chargement ou une modification du cache.</summary>
    event EventHandler? EntitiesChanged;

    /// <summary>(Re)charge le cache depuis la source de données.</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>Renvoie l'entité correspondant à la clé, ou null si elle n'est pas en cache.</summary>
    TEntity? Find(TKey key);

    /// <summary>Ajoute l'entité au cache, ou remplace celle qui a la même clé.</summary>
    void Track(TEntity entity);
}
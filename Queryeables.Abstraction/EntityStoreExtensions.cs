using System.Linq.Expressions;

namespace Queryeables.Abstraction;

/// <summary>Opérations composées au-dessus de <see cref="IEntityStore"/>, valables pour Dommel comme pour EF.</summary>
public static class EntityStoreExtensions
{
    /// <summary>
    /// Exécute <paramref name="work"/> dans une transaction : validée si tout réussit,
    /// annulée sinon. L'exception d'origine est relancée telle quelle.
    /// </summary>
    /// <example>
    /// <code>
    /// await using var store = DommelEntityStore.Create(factory);
    /// await store.RunInTransactionAsync(async s =>
    /// {
    ///     await s.UpdateAsync(commande);
    ///     await s.SyncChildrenAsync(anciennesLignes, commande.Lignes, l => l.Id == 0, (a, b) => a.Id == b.Id);
    /// });
    /// </code>
    /// </example>
    public static async Task RunInTransactionAsync(this IEntityStore store,
        Func<IEntityStore, Task> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(work);

        // Sans CommitAsync, le Dispose de la transaction l'annule.
        await using var transaction = await store.BeginTransactionAsync(ct);
        await work(store);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Aligne les enfants enregistrés sur leur nouvelle liste : supprime ceux qui ont disparu,
    /// insère les nouveaux et met à jour les autres. À appeler dans <see cref="RunInTransactionAsync"/>.
    /// </summary>
    /// <param name="before">Enfants tels qu'ils sont en base.</param>
    /// <param name="current">Enfants tels qu'ils doivent être.</param>
    /// <param name="isNew">Vrai pour un enfant pas encore en base (par exemple <c>c => c.Id == 0</c>).</param>
    /// <param name="matches">Vrai si deux enfants désignent la même ligne (par exemple <c>(a, b) => a.Id == b.Id</c>).</param>
    public static async Task SyncChildrenAsync<TChild>(this IEntityStore store,
        IEnumerable<TChild> before,
        IEnumerable<TChild> current,
        Func<TChild, bool> isNew,
        Func<TChild, TChild, bool> matches,
        CancellationToken ct = default)
        where TChild : class
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(isNew);
        ArgumentNullException.ThrowIfNull(matches);

        var currentList = current as IReadOnlyCollection<TChild> ?? current.ToList();

        // Suppressions d'abord : un enfant remplacé par un nouveau ne bloque pas une contrainte d'unicité.
        foreach (var old in before.ToList())
        {
            if (!currentList.Any(c => !isNew(c) && matches(c, old)))
                await store.DeleteAsync(old, ct);
        }

        foreach (var child in currentList)
        {
            if (isNew(child))
                await store.InsertAsync(child, ct);
            else
                await store.UpdateAsync(child, ct);
        }
    }

    // ---------- Lecture stricte et opérations groupées (idées des repositories ABP, voir CREDITS.md) ----------

    /// <summary>
    /// Comme <see cref="IEntityStore.FirstOrDefaultAsync{TEntity}"/>, mais lève une erreur NotFound
    /// au lieu de renvoyer null : pour le code qui considère l'absence comme anormale.
    /// </summary>
    public static async Task<TEntity> GetAsync<TEntity>(this IEntityStore store,
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(store);
        return await store.FirstOrDefaultAsync(predicate, ct)
            ?? throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TEntity).Name} introuvable.");
    }

    /// <summary>Insère toutes les entités dans une seule transaction (un seul appel avec le store distant).</summary>
    public static Task InsertManyAsync<TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
        CancellationToken ct = default) where TEntity : class
        => store.ForEachInTransactionAsync(entities, async (s, e) => { await s.InsertAsync(e, ct); }, ct);

    /// <summary>
    /// Met à jour toutes les entités dans une seule transaction. Si une ligne n'existe plus, rien n'est appliqué
    /// (erreur NotFound).
    /// </summary>
    public static Task UpdateManyAsync<TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
        CancellationToken ct = default) where TEntity : class
        => store.ForEachInTransactionAsync(entities, async (s, e) =>
        {
            if (!await s.UpdateAsync(e, ct))
                throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TEntity).Name} introuvable : rien n'a été mis à jour.");
        }, ct);

    /// <summary>
    /// Supprime toutes les entités dans une seule transaction. Si une ligne n'existe plus, rien n'est supprimé
    /// (erreur NotFound).
    /// </summary>
    public static Task DeleteManyAsync<TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
        CancellationToken ct = default) where TEntity : class
        => store.ForEachInTransactionAsync(entities, async (s, e) =>
        {
            if (!await s.DeleteAsync(e, ct))
                throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TEntity).Name} introuvable : rien n'a été supprimé.");
        }, ct);

    private static Task ForEachInTransactionAsync<TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
        Func<IEntityStore, TEntity, Task> action, CancellationToken ct) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(entities);
        var list = entities.ToList();
        if (list.Count == 0)
            return Task.CompletedTask;

        return store.RunInTransactionAsync(async s =>
        {
            foreach (var entity in list)
                await action(s, entity);
        }, ct);
    }
}

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
}

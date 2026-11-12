using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Abstraction;

/// <summary>
/// The work of an entity graph on a store given by the caller, inside a transaction the caller owns: a remote batch, or
/// several graphs saved together. <see cref="EntityGraph{TParent, TKey}"/> implements it.
/// </summary>
public interface IEntityGraph<[DynamicallyAccessedMembers(EntityMembers.Required)] TParent> where TParent : class
{
    /// <summary>Inserts or updates the parent and aligns its children. Throws <see cref="EntityStoreException"/> on failure.</summary>
    Task SaveAsync(IEntityStore store, TParent parent, CancellationToken ct = default);

    /// <summary>Deletes the children, then the parent. Throws <see cref="EntityStoreException"/> (NotFound) when it is gone.</summary>
    Task RemoveAsync(IEntityStore store, TParent parent, CancellationToken ct = default);

    /// <summary>Reads the children into the parent's collections.</summary>
    Task LoadAsync(IEntityStore store, TParent parent, CancellationToken ct = default);

    /// <summary>Reads the children of every parent of <paramref name="parents"/>: one query per relation and per level, not per parent.</summary>
    async Task LoadAsync(IEntityStore store, IReadOnlyList<TParent> parents, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parents);
        foreach (var parent in parents)
            await LoadAsync(store, parent, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="predicate"/> with its relation filters (<c>Any</c>, <c>All</c>, <c>Count</c> on a collection, a
    /// condition on a navigation) turned into conditions on columns, from queries on <paramref name="store"/>.
    /// </summary>
    Task<System.Linq.Expressions.Expression<Func<TParent, bool>>> ExpandAsync(IEntityStore store,
        System.Linq.Expressions.Expression<Func<TParent, bool>> predicate, CancellationToken ct = default)
        => throw new NotSupportedException($"This graph of {typeof(TParent).Name} does not turn relation filters into queries.");

    /// <summary>
    /// The foreign keys of the graph: which property of which type holds the key of which type (the children toward the
    /// parent, the links toward both sides, the parent toward its references). The offline mode rewrites them when a row
    /// created offline gets its real key.
    /// </summary>
    IReadOnlyList<GraphForeignKey> ForeignKeys => [];

    /// <summary>Remembers the values of the loaded children (<see cref="EntityOriginals"/>), at every level.</summary>
    void Remember(TParent parent)
    {
    }

    /// <summary>Adds the ids (<see cref="GraphChanges.IdOf"/>) of the children that still have the values they were read with.</summary>
    void CollectUnchanged(TParent parent, ISet<string> ids)
    {
    }
}

/// <summary>The property <paramref name="Property"/> of <paramref name="Owner"/> holds a key of <paramref name="Target"/>.</summary>
public sealed record GraphForeignKey(Type Owner, string Property, Type Target);

/// <summary>An <see cref="IAggregateRepository{TEntity}"/> backed by an entity graph (<see cref="GraphRepository{TParent, TKey}"/>).</summary>
public interface IGraphRepository<[DynamicallyAccessedMembers(EntityMembers.Required)] TParent> : IAggregateRepository<TParent>
    where TParent : class
{
    /// <summary>The graph, to run its work in a transaction opened elsewhere.</summary>
    IEntityGraph<TParent> Graph { get; }
}

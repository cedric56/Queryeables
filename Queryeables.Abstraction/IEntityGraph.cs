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
}

/// <summary>An <see cref="IDbTransaction{TEntity}"/> backed by an entity graph (<see cref="EntityGraphService{TParent, TKey}"/>).</summary>
public interface IEntityGraphService<[DynamicallyAccessedMembers(EntityMembers.Required)] TParent> : IDbTransaction<TParent>
    where TParent : class
{
    /// <summary>The graph, to run its work in a transaction opened elsewhere.</summary>
    IEntityGraph<TParent> Graph { get; }
}

using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Abstraction;

/// <summary>
/// Ready-made <see cref="IDbTransaction{TEntity}"/> for an <see cref="EntityGraph{TParent, TKey}"/>: saves and removes
/// the parent with its children in one transaction, and loads the children. Works with any store: Dommel, EF Core,
/// or a remote server.
/// </summary>
/// <remarks>
/// With a remote store factory (<see cref="IEntityStoreFactory.IsRemote"/>), save and remove send the parent only,
/// children included, in a single call: the server saves the children with its own graph service.
/// The source generator writes a <c>{Parent}Service</c> class on top of this one for every <c>[EntityGraph]</c>.
/// </remarks>
public class EntityGraphService<[DynamicallyAccessedMembers(EntityMembers.Required)] TParent, TKey> : IDbTransaction<TParent>
    where TParent : class
    where TKey : notnull
{
    private readonly Func<IEntityStore> _storeFactory;
    private readonly bool _childrenSavedByServer;

    /// <param name="stores">Creates a store per call; the service disposes it.</param>
    /// <param name="graph">The parent and its child collections.</param>
    /// <param name="childrenSavedByServer">Overrides <see cref="IEntityStoreFactory.IsRemote"/>.</param>
    public EntityGraphService(IEntityStoreFactory stores, EntityGraph<TParent, TKey> graph, bool? childrenSavedByServer = null)
        : this((stores ?? throw new ArgumentNullException(nameof(stores))).Create, graph,
            childrenSavedByServer ?? stores.IsRemote)
    {
    }

    /// <param name="storeFactory">Creates a store per call; the service disposes it.</param>
    /// <param name="graph">The parent and its child collections.</param>
    /// <param name="childrenSavedByServer">
    /// For a remote store whose server already saves the children: save and remove send the parent only.
    /// </param>
    public EntityGraphService(Func<IEntityStore> storeFactory, EntityGraph<TParent, TKey> graph, bool childrenSavedByServer = false)
    {
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _childrenSavedByServer = childrenSavedByServer;
    }

    public EntityGraph<TParent, TKey> Graph { get; }

    public virtual async Task<DbResult> SaveAsync(TParent entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await using var store = _storeFactory();

        if (_childrenSavedByServer)
            return await DbResult.RunAsync(() => store.SaveAsync(entity, Graph.IsNew, ct));

        return await store.RunInTransactionAsync(s => Graph.SaveAsync(s, entity, ct), ct);
    }

    public virtual async Task<DbResult> RemoveAsync(TParent entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await using var store = _storeFactory();

        if (_childrenSavedByServer)
            return await DbResult.RunAsync(async () =>
            {
                if (!await store.DeleteAsync(entity, ct))
                    throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TParent).Name} not found: nothing was deleted.");
            });

        return await store.RunInTransactionAsync(s => Graph.RemoveAsync(s, entity, ct), ct);
    }

    /// <summary>Reads the children of <paramref name="entity"/> into its collections (see <see cref="EntityGraph{TParent, TKey}.LoadAsync(IEntityStore, TParent, CancellationToken)"/>).</summary>
    public virtual async Task LoadChildrenAsync(TParent entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await using var store = _storeFactory();
        await Graph.LoadAsync(store, entity, ct);
    }

    /// <summary>Reads one child collection of <paramref name="entity"/>, for example <c>nameof(Person.Contacts)</c>.</summary>
    public virtual async Task LoadChildrenAsync(TParent entity, string collection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await using var store = _storeFactory();
        await Graph.LoadAsync(store, entity, collection, ct);
    }
}

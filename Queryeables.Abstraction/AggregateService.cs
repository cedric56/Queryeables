using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Abstraction;

/// <summary>
/// Ready-made <see cref="IDbTransaction{TEntity}"/> for an <see cref="Aggregate{TParent, TKey}"/>: saves and removes
/// the parent with its children in one transaction, and loads the children. Works with any store: Dommel, EF Core,
/// or a remote MagicOnion store.
/// </summary>
/// <param name="storeFactory">Creates a store per call; the service disposes it.</param>
/// <param name="aggregate">The parent and its child collections.</param>
/// <param name="childrenSavedByServer">
/// For a remote store whose server already saves the children (<c>EntityHandler</c> with <c>aggregate:</c>):
/// save and remove send the parent only, children included in it, in a single call. Loading is unchanged.
/// </param>
/// <example>
/// <code>
/// public sealed class PersonService(Func&lt;IEntityStore&gt; stores, Aggregate&lt;Person, long&gt; persons)
///     : AggregateService&lt;Person, long&gt;(stores, persons), IPersonService
/// {
///     public Task LoadContactsAsync(Person person, CancellationToken ct = default) => LoadChildrenAsync(person, ct);
/// }
/// </code>
/// </example>
public class AggregateService<[DynamicallyAccessedMembers(EntityMembers.Required)] TParent, TKey>(
    Func<IEntityStore> storeFactory,
    Aggregate<TParent, TKey> aggregate,
    bool childrenSavedByServer = false) : IDbTransaction<TParent>
    where TParent : class
    where TKey : notnull
{
    private readonly Func<IEntityStore> _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));

    public Aggregate<TParent, TKey> Aggregate { get; } = aggregate ?? throw new ArgumentNullException(nameof(aggregate));

    public virtual async Task<DBResult> SaveAsync(TParent entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await using var store = _storeFactory();

        if (childrenSavedByServer)
            return await DBResult.RunAsync(() => store.SaveAsync(entity, Aggregate.IsNew, ct));

        return await store.RunInTransactionAsync(s => Aggregate.SaveAsync(s, entity, ct), ct);
    }

    public virtual async Task<DBResult> RemoveAsync(TParent entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await using var store = _storeFactory();

        if (childrenSavedByServer)
            return await DBResult.RunAsync(async () =>
            {
                if (!await store.DeleteAsync(entity, ct))
                    throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TParent).Name} not found: nothing was deleted.");
            });

        return await store.RunInTransactionAsync(s => Aggregate.RemoveAsync(s, entity, ct), ct);
    }

    /// <summary>Reads the children of <paramref name="entity"/> into its collections (see <see cref="Aggregate{TParent, TKey}.LoadAsync"/>).</summary>
    public virtual async Task LoadChildrenAsync(TParent entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await using var store = _storeFactory();
        await Aggregate.LoadAsync(store, entity, ct);
    }
}

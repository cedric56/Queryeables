using System.Linq.Expressions;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Abstraction;

/// <summary>Composite operations on top of <see cref="IEntityStore"/>, valid for Dommel as well as EF.</summary>
public static class EntityStoreExtensions
{
    /// <summary>
    /// Runs <paramref name="work"/> in a transaction: committed if everything succeeds, rolled back otherwise.
    /// Never throws (except on cancellation): an exception thrown by <paramref name="work"/>, or a failed commit,
    /// is returned as a failed <see cref="DBResult"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// await using var store = DommelEntityStore.Create(factory);
    /// var result = await store.RunInTransactionAsync(async s =>
    /// {
    ///     await s.UpdateAsync(order);
    ///     await s.SyncChildrenAsync(previousLines, order.Lines, l => l.Id == 0, (a, b) => a.Id == b.Id);
    /// });
    /// if (!result.Successful) ...
    /// </code>
    /// </example>
    public static async Task<DBResult> RunInTransactionAsync(this IEntityStore store,
        Func<IEntityStore, Task> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return await store.RunInTransactionAsync(async s =>
        {
            await work(s);
            return true;
        }, ct);
    }

    /// <summary>
    /// Like <see cref="RunInTransactionAsync(IEntityStore, Func{IEntityStore, Task}, CancellationToken)"/>,
    /// and returns the value produced by <paramref name="work"/> once the transaction is committed.
    /// </summary>
    public static async Task<DBResult<T>> RunInTransactionAsync<T>(this IEntityStore store,
        Func<IEntityStore, Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(work);

        IEntityTransaction transaction;
        try
        {
            transaction = await store.BeginTransactionAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DBResult.FromException(ex).As<T>();
        }

        // Without a successful CommitAsync, disposing the transaction rolls it back.
        await using (transaction)
        {
            T value;
            try
            {
                value = await work(store);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return DBResult.FromException(ex).As<T>();
            }

            var commit = await transaction.CommitAsync(ct);
            return commit.Successful ? DBResult.Success(value) : commit.As<T>();
        }
    }

    /// <summary>
    /// Aligns the stored children with their new list: deletes those that disappeared,
    /// inserts the new ones and updates the others. Call it inside <see cref="RunInTransactionAsync(IEntityStore, Func{IEntityStore, Task}, CancellationToken)"/>.
    /// </summary>
    /// <param name="before">Children as they are in the database.</param>
    /// <param name="current">Children as they must be.</param>
    /// <param name="isNew">True for a child not yet in the database (for example <c>c => c.Id == 0</c>).</param>
    /// <param name="matches">True when two children refer to the same row (for example <c>(a, b) => a.Id == b.Id</c>).</param>
    public static async Task SyncChildrenAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TChild>(this IEntityStore store,
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

        // Deletes first: a child replaced by a new one does not violate a unique constraint.
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

    /// <summary>
    /// Inserts the entity when it is new, updates it otherwise. Throws <see cref="EntityStoreException"/> (NotFound)
    /// when an updated entity no longer exists.
    /// </summary>
    /// <param name="key">Primary key: a default value (0, Guid.Empty, null) means a new entity.</param>
    /// <example><code>await store.SaveAsync(person, p => p.Id);</code></example>
    public static Task SaveAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TKey>(this IEntityStore store,
        TEntity entity, Func<TEntity, TKey> key, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(key);
        return store.SaveAsync(entity, e => key(e) is not { } value || EqualityComparer<TKey>.Default.Equals(value, default!), ct);
    }

    /// <summary>
    /// Inserts the entity when <paramref name="isNew"/> says so, updates it otherwise. Throws
    /// <see cref="EntityStoreException"/> (NotFound) when an updated entity no longer exists.
    /// </summary>
    public static async Task SaveAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store,
        TEntity entity, Func<TEntity, bool> isNew, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(isNew);

        if (isNew(entity))
            await store.InsertAsync(entity, ct);
        else if (!await store.UpdateAsync(entity, ct))
            throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TEntity).Name} introuvable : rien n'a été mis à jour.");
    }

    // ---------- Strict read and bulk operations (ideas from ABP repositories, see CREDITS.md) ----------

    /// <summary>
    /// Like <see cref="IEntityStore.FirstOrDefaultAsync{TEntity}"/>, but throws a NotFound error
    /// instead of returning null: for code that treats absence as abnormal.
    /// </summary>
    public static async Task<TEntity> GetAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store,
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(store);
        return await store.FirstOrDefaultAsync(predicate, ct)
            ?? throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TEntity).Name} introuvable.");
    }

    /// <summary>Inserts all entities in a single transaction (a single call with the remote store).</summary>
    public static Task<DBResult> InsertManyAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
        CancellationToken ct = default) where TEntity : class
        => store.ForEachInTransactionAsync(entities, async (s, e) => { await s.InsertAsync(e, ct); }, ct);

    /// <summary>
    /// Updates all entities in a single transaction. If a row no longer exists, nothing is applied
    /// (NotFound failure).
    /// </summary>
    public static Task<DBResult> UpdateManyAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
        CancellationToken ct = default) where TEntity : class
        => store.ForEachInTransactionAsync(entities, async (s, e) =>
        {
            if (!await s.UpdateAsync(e, ct))
                throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TEntity).Name} introuvable : rien n'a été mis à jour.");
        }, ct);

    /// <summary>
    /// Deletes all entities in a single transaction. If a row no longer exists, nothing is deleted
    /// (NotFound failure).
    /// </summary>
    public static Task<DBResult> DeleteManyAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
        CancellationToken ct = default) where TEntity : class
        => store.ForEachInTransactionAsync(entities, async (s, e) =>
        {
            if (!await s.DeleteAsync(e, ct))
                throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TEntity).Name} introuvable : rien n'a été supprimé.");
        }, ct);

    private static Task<DBResult> ForEachInTransactionAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
        Func<IEntityStore, TEntity, Task> action, CancellationToken ct) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(entities);
        var list = entities.ToList();
        if (list.Count == 0)
            return Task.FromResult(DBResult.Success());

        return store.RunInTransactionAsync(async s =>
        {
            foreach (var entity in list)
                await action(s, entity);
        }, ct);
    }
}

using System.Linq.Expressions;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Abstraction;

/// <summary>Composite operations on top of <see cref="IEntityStore"/>, valid for Dommel as well as EF.</summary>
public static class EntityStoreExtensions
{
    /// <summary>
    /// Runs <paramref name="work"/> in a transaction: committed if everything succeeds, rolled back otherwise.
    /// Never throws (except on cancellation): an exception thrown by <paramref name="work"/>, or a failed commit,
    /// is returned as a failed <see cref="DbResult"/>.
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
    public static async Task<DbResult> RunInTransactionAsync(this IEntityStore store,
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
    public static async Task<DbResult<T>> RunInTransactionAsync<T>(this IEntityStore store,
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
            return DbResult.FromException(ex).As<T>();
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
                return DbResult.FromException(ex).As<T>();
            }

            var commit = await transaction.CommitAsync(ct);
            return commit.Successful ? DbResult.Success(value) : commit.As<T>();
        }
    }

    /// <summary>
    /// Aligns the stored children with their new list: deletes those that disappeared,
    /// inserts the new ones and updates the others. Throws <see cref="EntityStoreException"/> (NotFound) when a child to
    /// update no longer exists, so that the transaction is rolled back instead of losing the change. Call it inside <see cref="RunInTransactionAsync(IEntityStore, Func{IEntityStore, Task}, CancellationToken)"/>.
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
            else if (!await store.UpdateAsync(child, ct))
                throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TChild).Name} not found: nothing was updated.");
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
            throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TEntity).Name} not found: nothing was updated.");
    }

    // ---------- Members with a default implementation, reachable from a variable typed as a concrete store ----------
    // C# only exposes a default interface member through the interface: these forward to it, so that
    // `DommelEntityStore store; store.CountAsync(...)` compiles, and runs the store's own version when it has one.

    /// <inheritdoc cref="IEntityStore.CountAsync{TEntity}"/>
    public static Task<long> CountAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store,
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => store.CountAsync(predicate, ct);

    /// <inheritdoc cref="IEntityStore.SelectPagedAsync{TEntity}(Expression{Func{TEntity, bool}}, int, int, Sort{TEntity}, CancellationToken)"/>
    public static Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        this IEntityStore store, Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize, Sort<TEntity> sort,
        CancellationToken ct = default) where TEntity : class
        => store.SelectPagedAsync(predicate, pageNumber, pageSize, sort, ct);

    /// <inheritdoc cref="IEntityStore.SelectAsync{TEntity, TResult}(Expression{Func{TEntity, bool}}, Expression{Func{TEntity, TResult}}, CancellationToken)"/>
    public static Task<IReadOnlyList<TResult>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TResult>(
        this IEntityStore store, Expression<Func<TEntity, bool>> predicate, Expression<Func<TEntity, TResult>> selector,
        CancellationToken ct = default) where TEntity : class
        => store.SelectAsync(predicate, selector, ct);

    /// <summary>The entities matching the predicate, in the given order (sorted after reading: use paging for large sets).</summary>
    public static async Task<IReadOnlyList<TEntity>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        this IEntityStore store, Expression<Func<TEntity, bool>> predicate, Sort<TEntity> sort, CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sort);
        return sort.Apply(await store.SelectAsync(predicate, ct)).ToList();
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
            ?? throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TEntity).Name} not found.");
    }

    /// <summary>Inserts all entities in a single transaction (a single call with the remote store).</summary>
    public static Task<DbResult> InsertManyAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
        CancellationToken ct = default) where TEntity : class
        => store.ForEachInTransactionAsync(entities, async (s, e) => { await s.InsertAsync(e, ct); }, ct);

    /// <summary>
    /// Updates all entities in a single transaction. If a row no longer exists, nothing is applied
    /// (NotFound failure).
    /// </summary>
    public static Task<DbResult> UpdateManyAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
        CancellationToken ct = default) where TEntity : class
        => store.ForEachInTransactionAsync(entities, async (s, e) =>
        {
            if (!await s.UpdateAsync(e, ct))
                throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TEntity).Name} not found: nothing was updated.");
        }, ct);

    /// <summary>
    /// Deletes all entities in a single transaction. If a row no longer exists, nothing is deleted
    /// (NotFound failure).
    /// </summary>
    public static Task<DbResult> DeleteManyAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
        CancellationToken ct = default) where TEntity : class
        => store.ForEachInTransactionAsync(entities, async (s, e) =>
        {
            if (!await s.DeleteAsync(e, ct))
                throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TEntity).Name} not found: nothing was deleted.");
        }, ct);

    private static Task<DbResult> ForEachInTransactionAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
        Func<IEntityStore, TEntity, Task> action, CancellationToken ct) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(entities);
        var list = entities.ToList();
        if (list.Count == 0)
            return Task.FromResult(DbResult.Success());

        return store.RunInTransactionAsync(async s =>
        {
            foreach (var entity in list)
                await action(s, entity);
        }, ct);
    }
}

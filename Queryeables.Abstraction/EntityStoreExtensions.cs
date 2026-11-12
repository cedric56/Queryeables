using System.Linq.Expressions;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Abstraction;

/// <summary>Composite operations on top of <see cref="IEntityStore"/>, valid for Dommel as well as EF.</summary>
public static class EntityStoreExtensions
{
    /// <summary>
    /// Runs <paramref name="work"/> in a transaction: committed if everything succeeds, rolled back otherwise.
    /// Never throws (except on cancellation): an exception thrown by <paramref name="work"/>, or a failed commit,
    /// is returned as a failed <see cref="EntityResult"/>.
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
    public static async Task<EntityResult> RunInTransactionAsync(this IEntityStore store,
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
    public static async Task<EntityResult<T>> RunInTransactionAsync<T>(this IEntityStore store,
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
            return EntityResult.FromException(ex).As<T>();
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
                return EntityResult.FromException(ex).As<T>();
            }

            var commit = await transaction.CommitAsync(ct);
            return commit.Successful ? EntityResult.Success(value) : commit.As<T>();
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

    /// <inheritdoc cref="IEntityStore.AggregateAsync{TEntity}"/>
    public static Task<IReadOnlyList<AggregateRow>> AggregateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        this IEntityStore store, Expression<Func<TEntity, bool>> predicate, Aggregation<TEntity> aggregation, CancellationToken ct = default)
        where TEntity : class
        => store.AggregateAsync(predicate, aggregation, ct);

    /// <inheritdoc cref="IEntityStore.PatchAsync{TEntity}"/>
    public static Task<bool> PatchAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store,
        TEntity original, TEntity modified, CancellationToken ct = default) where TEntity : class
        => store.PatchAsync(original, modified, ct);

    /// <inheritdoc cref="IEntityStore.UpdateWhereAsync{TEntity}"/>
    /// <example><code>await store.UpdateWhereAsync&lt;Invoice&gt;(i =&gt; i.Year == 2024, set =&gt; set.Set(i =&gt; i.Archived, true));</code></example>
    public static Task<int> UpdateWhereAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store,
        Expression<Func<TEntity, bool>> predicate, Action<EntityUpdate<TEntity>> set, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(set);
        var update = new EntityUpdate<TEntity>();
        set(update);
        if (update.Values.Count == 0)
            throw new ArgumentException("Nothing to set.", nameof(set));
        return store.UpdateWhereAsync(predicate, update, ct);
    }

    /// <inheritdoc cref="IEntityStore.DeleteWhereAsync{TEntity}"/>
    public static Task<int> DeleteWhereAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store,
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => store.DeleteWhereAsync(predicate, ct);

    /// <summary>The sum of <paramref name="value"/> over the matching rows (0 without rows).</summary>
    public static async Task<decimal> SumAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TValue>(
        this IEntityStore store, Expression<Func<TEntity, bool>> predicate, Expression<Func<TEntity, TValue>> value, CancellationToken ct = default)
        where TEntity : class
        => await One(store, predicate, Aggregation.Of<TEntity>().Sum(value), ct).ConfigureAwait(false) is { } sum
            ? Convert.ToDecimal(sum, System.Globalization.CultureInfo.InvariantCulture)
            : 0m;

    /// <summary>The smallest value of <paramref name="value"/> over the matching rows; default without rows.</summary>
    public static async Task<TValue?> MinAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TValue>(
        this IEntityStore store, Expression<Func<TEntity, bool>> predicate, Expression<Func<TEntity, TValue>> value, CancellationToken ct = default)
        where TEntity : class
        => To<TValue>(await One(store, predicate, Aggregation.Of<TEntity>().Min(value), ct).ConfigureAwait(false));

    /// <summary>The largest value of <paramref name="value"/> over the matching rows; default without rows.</summary>
    public static async Task<TValue?> MaxAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TValue>(
        this IEntityStore store, Expression<Func<TEntity, bool>> predicate, Expression<Func<TEntity, TValue>> value, CancellationToken ct = default)
        where TEntity : class
        => To<TValue>(await One(store, predicate, Aggregation.Of<TEntity>().Max(value), ct).ConfigureAwait(false));

    /// <summary>The average of <paramref name="value"/> over the matching rows; null without rows.</summary>
    public static async Task<double?> AverageAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TValue>(
        this IEntityStore store, Expression<Func<TEntity, bool>> predicate, Expression<Func<TEntity, TValue>> value, CancellationToken ct = default)
        where TEntity : class
        => await One(store, predicate, Aggregation.Of<TEntity>().Average(value), ct).ConfigureAwait(false) is { } average
            ? Convert.ToDouble(average, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// The matching rows grouped by <paramref name="key"/>: each group with its number of rows, and the values of
    /// <paramref name="values"/> (optional) in order.
    /// </summary>
    /// <example><code>var perStatus = await store.GroupAsync&lt;Order, Status&gt;(o =&gt; true, o =&gt; o.Status, a =&gt; a.Sum(o =&gt; o.Total));</code></example>
    public static async Task<IReadOnlyList<Group<TKey>>> GroupAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TKey>(
        this IEntityStore store, Expression<Func<TEntity, bool>> predicate, Expression<Func<TEntity, TKey>> key,
        Action<Aggregation<TEntity>>? values = null, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(store);
        var aggregation = Aggregation.Of<TEntity>().GroupBy(key);
        values?.Invoke(aggregation);
        var rows = await store.AggregateAsync(predicate, aggregation, ct).ConfigureAwait(false);
        return [.. rows.Select(r => new Group<TKey>(To<TKey>(r.Key)!, r.Count, r.Values))];
    }

    /// <summary>
    /// The page after <paramref name="after"/> (the last row of the previous page; null for the first page), in the order of
    /// <paramref name="sort"/>, the key breaking ties: paging by position (keyset), as deep as wanted, at the cost of the
    /// first page. Rows inserted or deleted meanwhile do not shift the pages. The sort keys of the rows must not be null.
    /// </summary>
    public static Task<IReadOnlyList<TEntity>> SelectAfterAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        this IEntityStore store, Expression<Func<TEntity, bool>> predicate, Sort<TEntity> sort, TEntity? after, int pageSize,
        CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(sort);
        var full = sort.WithKey();
        var filter = after is null ? predicate : SyncMetadata.And(predicate, full.After(after));
        return store.SelectPagedAsync(filter, 1, pageSize, full, ct);
    }

    private static async Task<object?> One<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(IEntityStore store,
        Expression<Func<TEntity, bool>> predicate, Aggregation<TEntity> aggregation, CancellationToken ct) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(store);
        var rows = await store.AggregateAsync(predicate, aggregation, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0].Values[0];
    }

    private static T? To<T>(object? value)
    {
        if (value is null)
            return default;
        if (value is T typed)
            return typed;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)(target.IsEnum ? Enum.ToObject(target, value) : Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture));
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
    public static Task<EntityResult> InsertManyAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
        CancellationToken ct = default) where TEntity : class
        => store.ForEachInTransactionAsync(entities, async (s, e) => { await s.InsertAsync(e, ct); }, ct);

    /// <summary>
    /// Updates all entities in a single transaction. If a row no longer exists, nothing is applied
    /// (NotFound failure).
    /// </summary>
    public static Task<EntityResult> UpdateManyAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
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
    public static Task<EntityResult> DeleteManyAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
        CancellationToken ct = default) where TEntity : class
        => store.ForEachInTransactionAsync(entities, async (s, e) =>
        {
            if (!await s.DeleteAsync(e, ct))
                throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TEntity).Name} not found: nothing was deleted.");
        }, ct);

    private static Task<EntityResult> ForEachInTransactionAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store, IEnumerable<TEntity> entities,
        Func<IEntityStore, TEntity, Task> action, CancellationToken ct) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(entities);
        var list = entities.ToList();
        if (list.Count == 0)
            return Task.FromResult(EntityResult.Success());

        return store.RunInTransactionAsync(async s =>
        {
            foreach (var entity in list)
                await action(s, entity);
        }, ct);
    }
}

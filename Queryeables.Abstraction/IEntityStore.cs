using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables.Abstraction;

/// <summary>
/// Single data access point, independent of the framework (Dommel, EF Core, ...).
/// One instance = one unit of work (one connection / one DbContext).
/// Implementations manage the connection and the current transaction themselves.
/// </summary>
/// <remarks>
/// <para><b>Evolving this interface without breaking existing implementations:</b></para>
/// <list type="bullet">
/// <item>an operation composed of existing operations goes into an extension method
/// (see <see cref="EntityStoreExtensions"/>);</item>
/// <item>a new operation is added with a default implementation built on existing members
/// (see <see cref="CountAsync{TEntity}"/>). Each store then overrides it with an optimized version when it can;</item>
/// <item>a member without a default implementation is only added in a new major version.</item>
/// </list>
/// Every new implementation must pass the <c>EntityStoreContractTests</c> test suite.
/// <para>
/// Stores are disposable both ways, so that a dependency injection container disposing synchronously does not fail;
/// prefer registering an <see cref="IEntityStoreFactory"/> rather than a store.
/// </para>
/// </remarks>
public interface IEntityStore : IAsyncDisposable, IDisposable
{
    // Read
    Task<IReadOnlyList<TEntity>> GetAllAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(CancellationToken ct = default) where TEntity : class;

    Task<IReadOnlyList<TEntity>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken ct = default) where TEntity : class;

    Task<TEntity?> FirstOrDefaultAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken ct = default) where TEntity : class;

    /// <param name="pageNumber">Page number, starting at 1.</param>
    Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate,
        int pageNumber,
        int pageSize,
        CancellationToken ct = default) where TEntity : class;

    /// <summary>
    /// Whether <see cref="SelectPagedAsync{TEntity}(Expression{Func{TEntity, bool}}, int, int, Sort{TEntity}, CancellationToken)"/>
    /// sorts and pages in the database (true), or reads every matching row and sorts them in memory (false: the default
    /// implementation). A server bounds a sorted page read in memory as it bounds any read of every row.
    /// </summary>
    bool SortsInStore => false;

    /// <summary>
    /// A page of the entities matching the predicate, in the given order. Stores that can sort in the database
    /// (Dommel, EF Core, MongoDB, a remote server) override it, and <see cref="SortsInStore"/>; the default implementation
    /// reads the matching rows, then sorts and pages them in memory.
    /// </summary>
    /// <param name="pageNumber">Page number, starting at 1.</param>
    async Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate,
        int pageNumber,
        int pageSize,
        Sort<TEntity> sort,
        CancellationToken ct = default) where TEntity : class
    {
        Paging.Validate(pageNumber, pageSize);
        ArgumentNullException.ThrowIfNull(sort);
        var rows = await SelectAsync(predicate, ct);
        return sort.Apply(rows).Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
    }

    /// <summary>
    /// The entities matching the predicate, projected with <paramref name="selector"/> (a DTO, a few columns...). EF Core
    /// projects in the database; the default implementation reads the entities, then projects them in memory.
    /// </summary>
    async Task<IReadOnlyList<TResult>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TResult>(
        Expression<Func<TEntity, bool>> predicate,
        Expression<Func<TEntity, TResult>> selector,
        CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(selector);
        var project = selector.Compile();
        return (await SelectAsync(predicate, ct)).Select(project).ToList();
    }

    /// <summary>Number of entities matching the predicate.</summary>
    /// <remarks>
    /// The default implementation loads the entities to count them; stores that can count
    /// in the database (Dommel, EF) override it.
    /// </remarks>
    async Task<long> CountAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken ct = default) where TEntity : class
        => (await SelectAsync(predicate, ct)).Count;

    /// <summary>
    /// Rows modified since <paramref name="sinceUtc"/> (inclusive), soft-deleted ones included:
    /// used for incremental synchronization. Requires a [LastModified] property.
    /// </summary>
    /// <exception cref="NotSupportedException">The type has no [LastModified] property.</exception>
    Task<IReadOnlyList<TEntity>> SelectChangesAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(DateTime sinceUtc, CancellationToken ct = default)
        where TEntity : class
        => SelectAsync(SyncMetadata.ModifiedSince<TEntity>(sinceUtc), ct);

    /// <summary>
    /// The rows of <paramref name="predicate"/> modified since <paramref name="sinceUtc"/>, soft-deleted ones included, at
    /// most <paramref name="maxRows"/>: the filter and the bound go to the database, so that a server reads only what the
    /// caller may receive (its rows, under a <c>RowLevelSecurity</c>), never the whole table to filter it afterwards.
    /// </summary>
    /// <exception cref="NotSupportedException">The type has no [LastModified] property.</exception>
    Task<IReadOnlyList<TEntity>> SelectChangesAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(DateTime sinceUtc,
        Expression<Func<TEntity, bool>> predicate, int maxRows, CancellationToken ct = default)
        where TEntity : class
        => SelectPagedAsync(SyncMetadata.And(SyncMetadata.ModifiedSince<TEntity>(sinceUtc), predicate), 1, maxRows, ct);

    /// <summary>
    /// True when <see cref="AggregateAsync{TEntity}"/> computes in the database (GROUP BY): the rows never reach this process.
    /// False by default: the values of every matching row are read, and a server counts them first (at most its read limit).
    /// </summary>
    bool AggregatesInStore => false;

    /// <summary>
    /// Values computed over the rows matching <paramref name="predicate"/>, for all of them or by group: counts, sums,
    /// minimums, maximums, averages. By default, the store reads only the values needed (a projection) and computes them;
    /// a remote store has the server compute them.
    /// </summary>
    async Task<IReadOnlyList<AggregateRow>> AggregateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate,
        Aggregation<TEntity> aggregation,
        CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(aggregation);
        if (aggregation.Projection() is not { } projection)
        {
            var count = await CountAsync(predicate, ct).ConfigureAwait(false);
            return [new AggregateRow(null, count, [.. aggregation.Values.Select(_ => (object?)count)])];
        }
        return aggregation.Compute(await SelectAsync(predicate, projection, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Writes the columns <paramref name="modified"/> changed from <paramref name="original"/>, and only them, over the
    /// stored row: what others changed meanwhile in other columns is kept. A column changed both here and by someone else
    /// is a conflict (<see cref="EntityStoreException"/>, Conflict). False when the row no longer exists. Afterwards,
    /// <paramref name="modified"/> holds the stored row (the others' changes, the new version).
    /// </summary>
    /// <remarks>Run it in a transaction to read and write the row atomically; a version ([ConcurrencyCheck]) makes it safe without.</remarks>
    async Task<bool> PatchAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        TEntity original,
        TEntity modified,
        CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        if (await FirstOrDefaultAsync(SyncMetadata.SameKey(modified), ct).ConfigureAwait(false) is not { } stored)
            return false;
        if (EntityPatch.Merge(stored, original, modified, EntityValues.Changed(original, modified))
            && !await UpdateAsync(stored, ct).ConfigureAwait(false))
            return false;
        EntityPatch.CopyColumns(stored, modified);
        return true;
    }

    /// <summary>
    /// Assigns the values of <paramref name="update"/> to every row matching <paramref name="predicate"/>, and returns their
    /// number. Each row is written as an update: versions, [LastModified], validation on a server, and notifications apply.
    /// A remote store has the server do it in one call and one transaction.
    /// </summary>
    async Task<int> UpdateWhereAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate,
        EntityUpdate<TEntity> update,
        CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(update);
        var rows = await SelectAsync(predicate, ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            update.Apply(row);
            if (!await UpdateAsync(row, ct).ConfigureAwait(false))
                throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TEntity).Name} not found: nothing was updated.");
        }
        return rows.Count;
    }

    /// <summary>
    /// Deletes every row matching <paramref name="predicate"/> (a soft delete where the type has one), and returns their
    /// number. A remote store has the server do it in one call and one transaction.
    /// </summary>
    async Task<int> DeleteWhereAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken ct = default) where TEntity : class
    {
        var rows = await SelectAsync(predicate, ct).ConfigureAwait(false);
        var deleted = 0;
        foreach (var row in rows)
        {
            if (await DeleteAsync(row, ct).ConfigureAwait(false))
                deleted++;
        }
        return deleted;
    }

    // Write
    /// <summary>Inserts the entity. A key generated by the database is written back into the entity.</summary>
    Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class;

    /// <returns>true if a row was updated.</returns>
    Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class;

    /// <returns>true if a row was deleted.</returns>
    Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class;

    // Transactions
    /// <summary>
    /// Starts a transaction. Every operation of the store goes through it
    /// until its Commit/Rollback/Dispose. Without Commit, Dispose rolls back.
    /// </summary>
    Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default);
}

/// <summary>
/// Transaction of an <see cref="IEntityStore"/>. Commit and rollback do not throw: they return a <see cref="EntityResult"/>
/// (a remote commit, for example, can fail on a conflict or a validation error).
/// </summary>
public interface IEntityTransaction : IAsyncDisposable
{
    Task<EntityResult> CommitAsync(CancellationToken ct = default);
    Task<EntityResult> RollbackAsync(CancellationToken ct = default);
}

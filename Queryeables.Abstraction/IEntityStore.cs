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
    /// A page of the entities matching the predicate, in the given order. Stores that can sort in the database
    /// (EF Core, a remote server) override it; the default implementation reads the matching rows, then sorts and pages
    /// them in memory.
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
/// Transaction of an <see cref="IEntityStore"/>. Commit and rollback do not throw: they return a <see cref="DbResult"/>
/// (a remote commit, for example, can fail on a conflict or a validation error).
/// </summary>
public interface IEntityTransaction : IAsyncDisposable
{
    Task<DbResult> CommitAsync(CancellationToken ct = default);
    Task<DbResult> RollbackAsync(CancellationToken ct = default);
}

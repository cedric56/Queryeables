using System.Linq.Expressions;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Abstraction;

/// <summary>
/// Wraps any <see cref="IEntityStore"/> (Dommel, EF...) to apply, on the types that have them:
/// <list type="bullet">
/// <item>[LastModified]: timestamp updated on every insert, update and soft delete;</item>
/// <item>[SoftDelete]: DeleteAsync flags the row instead of deleting it, and reads exclude it.</item>
/// </list>
/// <see cref="SelectChangesAsync{TEntity}"/>, on the contrary, returns every modified row, deleted ones included:
/// that is what a client needs to synchronize.
/// Types without these attributes pass through unchanged.
/// </summary>
/// <example>
/// <code>
/// var store = DommelEntityStore.Create(factory).WithSyncConventions();
/// </code>
/// </example>
/// <remarks>Idea taken from the Datasync Community Toolkit (UpdatedAt, Deleted, incremental synchronization), see CREDITS.md.</remarks>
public sealed class SyncConventionsStore(IEntityStore inner, TimeProvider? clock = null) : IEntityStore
{
    private readonly IEntityStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    // ---------- Read: soft-deleted rows are excluded ----------

    public Task<IReadOnlyList<TEntity>> GetAllAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(CancellationToken ct = default) where TEntity : class
        => SyncMetadata.SoftDelete(typeof(TEntity)) is null
            ? _inner.GetAllAsync<TEntity>(ct)
            : _inner.SelectAsync(SyncMetadata.NotDeleted<TEntity>(null), ct);

    public Task<IReadOnlyList<TEntity>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default)
        where TEntity : class
        => _inner.SelectAsync(SyncMetadata.NotDeleted(predicate), ct);

    public Task<TEntity?> FirstOrDefaultAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default)
        where TEntity : class
        => _inner.FirstOrDefaultAsync(SyncMetadata.NotDeleted(predicate), ct);

    public Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(Expression<Func<TEntity, bool>> predicate,
        int pageNumber, int pageSize, CancellationToken ct = default) where TEntity : class
        => _inner.SelectPagedAsync(SyncMetadata.NotDeleted(predicate), pageNumber, pageSize, ct);

    public Task<long> CountAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default)
        where TEntity : class
        => _inner.CountAsync(SyncMetadata.NotDeleted(predicate), ct);

    /// <summary>Rows modified since <paramref name="sinceUtc"/>, soft-deleted ones included.</summary>
    public Task<IReadOnlyList<TEntity>> SelectChangesAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(DateTime sinceUtc, CancellationToken ct = default)
        where TEntity : class
        => _inner.SelectChangesAsync<TEntity>(sinceUtc, ct);

    // ---------- Write ----------

    public Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        SyncMetadata.Touch(entity, UtcNow);
        return _inner.InsertAsync(entity, ct);
    }

    public Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        SyncMetadata.Touch(entity, UtcNow);
        return _inner.UpdateAsync(entity, ct);
    }

    public async Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (SyncMetadata.SoftDelete(typeof(TEntity)) is null)
            return await _inner.DeleteAsync(entity, ct);

        if (SyncMetadata.IsDeleted(entity))
            return false; // already deleted, like a second DeleteAsync on a vanished row

        // Soft delete: an update, hence with a version check when the entity has one.
        var previous = SyncMetadata.LastModifiedUtc(entity);
        SyncMetadata.SetDeleted(entity, true);
        SyncMetadata.Touch(entity, UtcNow);
        try
        {
            if (await _inner.UpdateAsync(entity, ct))
                return true;
        }
        catch
        {
            Restore();
            throw;
        }

        Restore();
        return false;

        void Restore()
        {
            SyncMetadata.SetDeleted(entity, false);
            if (previous is { } date)
                SyncMetadata.Touch(entity, date);
        }
    }

    // ---------- Transactions ----------

    public Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default) => _inner.BeginTransactionAsync(ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

public static class SyncConventionsStoreExtensions
{
    /// <summary>Applies [LastModified] and [SoftDelete] (see <see cref="SyncConventionsStore"/>).</summary>
    public static IEntityStore WithSyncConventions(this IEntityStore store, TimeProvider? clock = null)
        => store as SyncConventionsStore ?? new SyncConventionsStore(store, clock);
}

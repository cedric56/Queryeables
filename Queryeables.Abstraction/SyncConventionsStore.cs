using System.Linq.Expressions;

namespace Queryeables.Abstraction;

/// <summary>
/// Enveloppe n'importe quel <see cref="IEntityStore"/> (Dommel, EF...) pour appliquer, sur les types qui en ont :
/// <list type="bullet">
/// <item>[LastModified] : date mise à jour à chaque insertion, modification et suppression logique ;</item>
/// <item>[SoftDelete] : DeleteAsync marque la ligne au lieu de la supprimer, et les lectures l'excluent.</item>
/// </list>
/// <see cref="SelectChangesAsync{TEntity}"/> renvoie au contraire toutes les lignes modifiées, supprimées comprises :
/// c'est ce dont un client a besoin pour se synchroniser.
/// Les types sans ces attributs passent tels quels.
/// </summary>
/// <example>
/// <code>
/// var store = DommelEntityStore.Create(factory).WithSyncConventions();
/// </code>
/// </example>
/// <remarks>Idée reprise de Datasync Community Toolkit (UpdatedAt, Deleted, synchronisation incrémentale), voir CREDITS.md.</remarks>
public sealed class SyncConventionsStore(IEntityStore inner, TimeProvider? clock = null) : IEntityStore
{
    private readonly IEntityStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    // ---------- Lecture : les lignes supprimées logiquement sont exclues ----------

    public Task<IReadOnlyList<TEntity>> GetAllAsync<TEntity>(CancellationToken ct = default) where TEntity : class
        => SyncMetadata.SoftDelete(typeof(TEntity)) is null
            ? _inner.GetAllAsync<TEntity>(ct)
            : _inner.SelectAsync(SyncMetadata.NotDeleted<TEntity>(null), ct);

    public Task<IReadOnlyList<TEntity>> SelectAsync<TEntity>(Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default)
        where TEntity : class
        => _inner.SelectAsync(SyncMetadata.NotDeleted(predicate), ct);

    public Task<TEntity?> FirstOrDefaultAsync<TEntity>(Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default)
        where TEntity : class
        => _inner.FirstOrDefaultAsync(SyncMetadata.NotDeleted(predicate), ct);

    public Task<IReadOnlyList<TEntity>> SelectPagedAsync<TEntity>(Expression<Func<TEntity, bool>> predicate,
        int pageNumber, int pageSize, CancellationToken ct = default) where TEntity : class
        => _inner.SelectPagedAsync(SyncMetadata.NotDeleted(predicate), pageNumber, pageSize, ct);

    public Task<long> CountAsync<TEntity>(Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default)
        where TEntity : class
        => _inner.CountAsync(SyncMetadata.NotDeleted(predicate), ct);

    /// <summary>Lignes modifiées depuis <paramref name="sinceUtc"/>, y compris celles supprimées logiquement.</summary>
    public Task<IReadOnlyList<TEntity>> SelectChangesAsync<TEntity>(DateTime sinceUtc, CancellationToken ct = default)
        where TEntity : class
        => _inner.SelectChangesAsync<TEntity>(sinceUtc, ct);

    // ---------- Écriture ----------

    public Task InsertAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        SyncMetadata.Touch(entity, UtcNow);
        return _inner.InsertAsync(entity, ct);
    }

    public Task<bool> UpdateAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        SyncMetadata.Touch(entity, UtcNow);
        return _inner.UpdateAsync(entity, ct);
    }

    public async Task<bool> DeleteAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (SyncMetadata.SoftDelete(typeof(TEntity)) is null)
            return await _inner.DeleteAsync(entity, ct);

        if (SyncMetadata.IsDeleted(entity))
            return false; // déjà supprimée, comme un second DeleteAsync sur une ligne disparue

        // Suppression logique : une mise à jour, donc avec contrôle de version si l'entité en a une.
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
    /// <summary>Applique [LastModified] et [SoftDelete] (voir <see cref="SyncConventionsStore"/>).</summary>
    public static IEntityStore WithSyncConventions(this IEntityStore store, TimeProvider? clock = null)
        => store as SyncConventionsStore ?? new SyncConventionsStore(store, clock);
}

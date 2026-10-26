using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Queryeables.Abstraction;

/// <summary>
/// Point d'accès unique aux données, indépendant du framework (Dommel, EF Core, ...).
/// Une instance = une unité de travail (une connexion / un DbContext).
/// Les implémentations gèrent elles-mêmes la connexion et la transaction en cours.
/// </summary>
public interface IEntityStore : IAsyncDisposable
{
    // Lecture
    Task<IReadOnlyList<TEntity>> SelectAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken ct = default) where TEntity : class;

    Task<TEntity?> FirstOrDefaultAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken ct = default) where TEntity : class;

    /// <param name="pageNumber">Numéro de page, commence à 1.</param>
    Task<IReadOnlyList<TEntity>> SelectPagedAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate,
        int pageNumber,
        int pageSize,
        CancellationToken ct = default) where TEntity : class;

    // Écriture
    /// <summary>Insère l'entité. Une clé générée par la base est réinjectée dans l'entité.</summary>
    Task InsertAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class;

    /// <returns>true si une ligne a été modifiée.</returns>
    Task<bool> UpdateAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class;

    /// <returns>true si une ligne a été supprimée.</returns>
    Task<bool> DeleteAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class;

    // Transactions
    /// <summary>
    /// Démarre une transaction. Toutes les opérations du store passent par elle
    /// jusqu'à son Commit/Rollback/Dispose. Sans Commit, le Dispose annule.
    /// </summary>
    Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default);
}

public interface IEntityTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);
}

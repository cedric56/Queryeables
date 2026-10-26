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
/// <remarks>
/// <para><b>Faire évoluer cette interface sans casser les implémentations existantes :</b></para>
/// <list type="bullet">
/// <item>une opération composée d'opérations existantes va dans une méthode d'extension
/// (voir <see cref="EntityStoreExtensions"/>) ;</item>
/// <item>une nouvelle opération est ajoutée avec une implémentation par défaut, construite sur les membres existants
/// (voir <see cref="CountAsync{TEntity}"/>). Chaque store la remplace ensuite par une version optimisée, quand il le peut ;</item>
/// <item>un membre sans implémentation par défaut ne s'ajoute qu'avec une nouvelle version majeure.</item>
/// </list>
/// Toute nouvelle implémentation doit passer la suite de tests <c>EntityStoreContractTests</c>.
/// </remarks>
public interface IEntityStore : IAsyncDisposable
{
    // Lecture
    Task<IReadOnlyList<TEntity>> GetAllAsync<TEntity>(CancellationToken ct = default) where TEntity : class;

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

    /// <summary>Nombre d'entités qui vérifient le prédicat.</summary>
    /// <remarks>
    /// L'implémentation par défaut charge les entités pour les compter ; les stores qui savent compter
    /// côté base (Dommel, EF) la remplacent.
    /// </remarks>
    async Task<long> CountAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken ct = default) where TEntity : class
        => (await SelectAsync(predicate, ct)).Count;

    /// <summary>
    /// Lignes modifiées depuis <paramref name="sinceUtc"/> (inclus), y compris celles supprimées logiquement :
    /// sert à la synchronisation incrémentale. Exige une propriété [LastModified].
    /// </summary>
    /// <exception cref="NotSupportedException">Le type n'a pas de propriété [LastModified].</exception>
    Task<IReadOnlyList<TEntity>> SelectChangesAsync<TEntity>(DateTime sinceUtc, CancellationToken ct = default)
        where TEntity : class
        => SelectAsync(SyncMetadata.ModifiedSince<TEntity>(sinceUtc), ct);

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

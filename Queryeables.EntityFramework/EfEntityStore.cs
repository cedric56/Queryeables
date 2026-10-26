using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Queryeables.Abstraction;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Queryeables.EntityFramework;

/// <summary>
/// Implémentation EF Core de IEntityStore.
/// À placer idéalement dans un projet séparé (Avae.DAL.EntityFramework)
/// pour que Avae.DAL ne dépende pas d'EF.
/// Comportement aligné sur Dommel : lectures sans suivi, chaque écriture est enregistrée immédiatement.
/// </summary>
public sealed class EfEntityStore : IEntityStore
{
    private readonly DbContext _context;
    private readonly bool _ownsContext;

    public EfEntityStore(DbContext context, bool ownsContext = true)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ownsContext = ownsContext;
    }

    // ---------- Lecture ----------

    public async Task<IReadOnlyList<TEntity>> GetAllAsync<TEntity>(CancellationToken ct = default) where TEntity : class
        => await _context.Set<TEntity>().AsNoTracking().ToListAsync(ct);

    public async Task<IReadOnlyList<TEntity>> SelectAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => await _context.Set<TEntity>().AsNoTracking().Where(predicate).ToListAsync(ct);

    public Task<TEntity?> FirstOrDefaultAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => _context.Set<TEntity>().AsNoTracking().FirstOrDefaultAsync(predicate, ct);

    public async Task<IReadOnlyList<TEntity>> SelectPagedAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize,
        CancellationToken ct = default) where TEntity : class
    {
        Paging.Validate(pageNumber, pageSize);

        var query = OrderByKey(_context.Set<TEntity>().AsNoTracking().Where(predicate));
        return await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
    }

    public Task<long> CountAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => _context.Set<TEntity>().LongCountAsync(predicate, ct);

    // ---------- Écriture ----------

    public async Task InsertAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        _context.Add(entity);
        await SaveAndDetachAsync(entity, ct); // EF remplit la clé générée
    }

    /// <exception cref="EntityStoreException">Conflict si l'entité a une version ([ConcurrencyCheck]) périmée.</exception>
    public async Task<bool> UpdateAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var entry = _context.Update(entity);

        if (ConcurrencyToken.Find(typeof(TEntity)) is not { } version)
            return await SaveAndDetachAsync(entity, ct) > 0;

        // EF vérifie l'ancienne version (WHERE Version = ...) et enregistre la nouvelle.
        var current = ConcurrencyToken.Get(entity, version);
        var token = entry.Property(version.Name);
        token.OriginalValue = ConcurrencyToken.Box(version, current);
        token.CurrentValue = ConcurrencyToken.Box(version, current + 1);

        if (await SaveAndDetachAsync(entity, ct) > 0)
            return true;

        ConcurrencyToken.Set(entity, version, current); // l'entité garde la version qu'elle avait
        return await ExistsAsync(entity, ct) ? throw ConcurrencyToken.ConflictFor(typeof(TEntity)) : false;
    }

    /// <exception cref="EntityStoreException">Conflict si l'entité a une version ([ConcurrencyCheck]) périmée.</exception>
    public async Task<bool> DeleteAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var entry = _context.Remove(entity);

        if (ConcurrencyToken.Find(typeof(TEntity)) is not { } version)
            return await SaveAndDetachAsync(entity, ct) > 0;

        entry.Property(version.Name).OriginalValue = ConcurrencyToken.Box(version, ConcurrencyToken.Get(entity, version));

        if (await SaveAndDetachAsync(entity, ct) > 0)
            return true;

        return await ExistsAsync(entity, ct) ? throw ConcurrencyToken.ConflictFor(typeof(TEntity)) : false;
    }

    /// <summary>La ligne de cette clé existe-t-elle encore ? (distingue un conflit d'une ligne supprimée)</summary>
    private async Task<bool> ExistsAsync<TEntity>(TEntity entity, CancellationToken ct) where TEntity : class
    {
        var key = _context.Model.FindEntityType(typeof(TEntity))?.FindPrimaryKey()
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} n'a pas de clé primaire.");
        var values = key.Properties.Select(p => p.PropertyInfo?.GetValue(entity)).ToArray();

        var found = await _context.Set<TEntity>().FindAsync(values, ct);
        if (found is null)
            return false;

        _context.Entry(found).State = EntityState.Detached;
        return true;
    }

    // ---------- Transactions ----------

    public async Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => new Transaction(await _context.Database.BeginTransactionAsync(ct));

    public ValueTask DisposeAsync() => _ownsContext ? _context.DisposeAsync() : ValueTask.CompletedTask;

    // ---------- Interne ----------

    private async Task<int> SaveAndDetachAsync<TEntity>(TEntity entity, CancellationToken ct) where TEntity : class
    {
        try
        {
            return await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return 0; // aucune ligne touchée, comme Dommel qui renvoie false
        }
        finally
        {
            _context.Entry(entity).State = EntityState.Detached;
        }
    }

    /// <summary>La pagination exige un ordre stable : on trie par clé primaire.</summary>
    private IQueryable<TEntity> OrderByKey<TEntity>(IQueryable<TEntity> query) where TEntity : class
    {
        var key = _context.Model.FindEntityType(typeof(TEntity))?.FindPrimaryKey()
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} n'a pas de clé primaire.");

        IOrderedQueryable<TEntity>? ordered = null;
        foreach (var property in key.Properties)
        {
            var name = property.Name;
            ordered = ordered is null
                ? query.OrderBy(e => EF.Property<object>(e, name))
                : ordered.ThenBy(e => EF.Property<object>(e, name));
        }
        return ordered!;
    }

    private sealed class Transaction : IEntityTransaction
    {
        private readonly IDbContextTransaction _inner;
        public Transaction(IDbContextTransaction inner) => _inner = inner;

        public Task CommitAsync(CancellationToken ct = default) => _inner.CommitAsync(ct);
        public Task RollbackAsync(CancellationToken ct = default) => _inner.RollbackAsync(ct);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Queryeables.Abstraction;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables.EntityFramework;

/// <summary>
/// EF Core implementation of IEntityStore.
/// Behavior aligned with Dommel: no-tracking reads, every write is saved immediately.
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

    // ---------- Read ----------

    public async Task<IReadOnlyList<TEntity>> GetAllAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(CancellationToken ct = default) where TEntity : class
        => await _context.Set<TEntity>().AsNoTracking().ToListAsync(ct);

    public async Task<IReadOnlyList<TEntity>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => await _context.Set<TEntity>().AsNoTracking().Where(predicate).ToListAsync(ct);

    public Task<TEntity?> FirstOrDefaultAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => _context.Set<TEntity>().AsNoTracking().FirstOrDefaultAsync(predicate, ct);

    public async Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
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

    public Task<long> CountAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => _context.Set<TEntity>().LongCountAsync(predicate, ct);

    // ---------- Write ----------

    public async Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        _context.Add(entity);
        await SaveAndDetachAsync(entity, ct); // EF fills in the generated key
    }

    /// <exception cref="EntityStoreException">Conflict when the entity has a stale version ([ConcurrencyCheck]).</exception>
    public async Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var entry = _context.Update(entity);

        if (ConcurrencyToken.Find(typeof(TEntity)) is not { } version)
            return await SaveAndDetachAsync(entity, ct) > 0;

        // EF checks the old version (WHERE Version = ...) and saves the new one.
        var current = ConcurrencyToken.Get(entity, version);
        var token = entry.Property(version.Name);
        token.OriginalValue = ConcurrencyToken.Box(version, current);
        token.CurrentValue = ConcurrencyToken.Box(version, current + 1);

        if (await SaveAndDetachAsync(entity, ct) > 0)
            return true;

        ConcurrencyToken.Set(entity, version, current); // the entity keeps the version it had
        return await ExistsAsync(entity, ct) ? throw ConcurrencyToken.ConflictFor(typeof(TEntity)) : false;
    }

    /// <exception cref="EntityStoreException">Conflict when the entity has a stale version ([ConcurrencyCheck]).</exception>
    public async Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
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

    /// <summary>Does the row with this key still exist? (tells a conflict from a deleted row)</summary>
    private async Task<bool> ExistsAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct) where TEntity : class
    {
        var key = _context.Model.FindEntityType(typeof(TEntity))?.FindPrimaryKey()
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no primary key.");
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

    // ---------- Internals ----------

    private async Task<int> SaveAndDetachAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct) where TEntity : class
    {
        try
        {
            return await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return 0; // no row affected, like Dommel returning false
        }
        finally
        {
            _context.Entry(entity).State = EntityState.Detached;
        }
    }

    /// <summary>Paging requires a stable order: sort by primary key.</summary>
    private IQueryable<TEntity> OrderByKey<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(IQueryable<TEntity> query) where TEntity : class
    {
        var key = _context.Model.FindEntityType(typeof(TEntity))?.FindPrimaryKey()
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no primary key.");

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

        public Task<DBResult> CommitAsync(CancellationToken ct = default) => DBResult.RunAsync(() => _inner.CommitAsync(ct));
        public Task<DBResult> RollbackAsync(CancellationToken ct = default) => DBResult.RunAsync(() => _inner.RollbackAsync(ct));
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}

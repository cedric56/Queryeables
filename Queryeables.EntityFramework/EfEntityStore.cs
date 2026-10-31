using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
/// Behavior aligned with Dommel: no-tracking reads, every write is saved immediately, and a write only concerns the
/// entity itself (and its owned types), never the entities it references: children and navigation properties are
/// written by their own calls, like <see cref="EntityGraph{TParent, TKey}"/> does.
/// </summary>
[RequiresUnreferencedCode(EfAot.Trimming)]
[RequiresDynamicCode(EfAot.DynamicCode)]
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

    /// <summary>Sorted and paged in the database; the primary key breaks ties, so that pages are stable.</summary>
    public async Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize, Sort<TEntity> sort,
        CancellationToken ct = default) where TEntity : class
    {
        Paging.Validate(pageNumber, pageSize);
        ArgumentNullException.ThrowIfNull(sort);

        IQueryable<TEntity> query = _context.Set<TEntity>().AsNoTracking().Where(predicate);
        IOrderedQueryable<TEntity>? ordered = null;
        foreach (var key in sort.Keys)
            ordered = Order(query, ordered, key.Property, key.Descending);

        var primaryKey = _context.Model.FindEntityType(typeof(TEntity))?.FindPrimaryKey()
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no primary key.");
        foreach (var property in primaryKey.Properties.Where(p => sort.Keys.All(k => k.Property != p.Name)))
            ordered = Order(query, ordered, property.Name, descending: false);

        return await (ordered ?? query)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
    }

    /// <summary>Projected in the database: only the selected columns are read.</summary>
    public async Task<IReadOnlyList<TResult>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TResult>(
        Expression<Func<TEntity, bool>> predicate, Expression<Func<TEntity, TResult>> selector, CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(selector);
        return await _context.Set<TEntity>().AsNoTracking().Where(predicate).Select(selector).ToListAsync(ct);
    }

    private static IOrderedQueryable<TEntity> Order<TEntity>(IQueryable<TEntity> query, IOrderedQueryable<TEntity>? ordered,
        string property, bool descending) where TEntity : class
        => (ordered, descending) switch
        {
            (null, false) => query.OrderBy(e => EF.Property<object>(e, property)),
            (null, true) => query.OrderByDescending(e => EF.Property<object>(e, property)),
            (_, false) => ordered.ThenBy(e => EF.Property<object>(e, property)),
            (_, true) => ordered.ThenByDescending(e => EF.Property<object>(e, property)),
        };

    public Task<long> CountAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => _context.Set<TEntity>().LongCountAsync(predicate, ct);

    // ---------- Write ----------

    public async Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var entries = Attach(entity, EntityState.Added);
        await SaveAndDetachAsync(entries, ct); // EF fills in the generated key
    }

    /// <exception cref="EntityStoreException">Conflict when the entity has a stale version ([ConcurrencyCheck]).</exception>
    public async Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var entries = Attach(entity, EntityState.Modified);
        var entry = entries[0];

        if (ConcurrencyToken.Find(typeof(TEntity)) is not { } version)
            return await SaveAndDetachAsync(entries, ct) > 0;

        // EF checks the old version (WHERE Version = ...) and saves the new one.
        var current = ConcurrencyToken.Get(entity, version);
        var token = entry.Property(version.Name);
        token.OriginalValue = ConcurrencyToken.Box(version, current);
        token.CurrentValue = ConcurrencyToken.Box(version, current + 1);

        if (await SaveAndDetachAsync(entries, ct) > 0)
            return true;

        ConcurrencyToken.Set(entity, version, current); // the entity keeps the version it had
        return await ExistsAsync(entity, ct) ? throw ConcurrencyToken.ConflictFor(typeof(TEntity)) : false;
    }

    /// <exception cref="EntityStoreException">Conflict when the entity has a stale version ([ConcurrencyCheck]).</exception>
    public async Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var entries = Attach(entity, EntityState.Deleted);
        var entry = entries[0];

        if (ConcurrencyToken.Find(typeof(TEntity)) is not { } version)
            return await SaveAndDetachAsync(entries, ct) > 0;

        entry.Property(version.Name).OriginalValue = ConcurrencyToken.Box(version, ConcurrencyToken.Get(entity, version));

        if (await SaveAndDetachAsync(entries, ct) > 0)
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

    public void Dispose()
    {
        if (_ownsContext)
            _context.Dispose();
    }

    // ---------- Internals ----------

    /// <summary>
    /// Tracks the entity with <paramref name="state"/>, with its owned types, and nothing else. Add, Update and Remove
    /// would walk the whole graph: a parent's children would be inserted twice, and a navigation property pointing to a
    /// cached entity would write that entity back.
    /// </summary>
    private List<EntityEntry> Attach(object entity, EntityState state)
    {
        var entries = new List<EntityEntry>();
        _context.ChangeTracker.TrackGraph(entity, node =>
        {
            // Not setting a state stops the walk at this node.
            if (node.SourceEntry is null || node.Entry.Metadata.IsOwned())
            {
                node.Entry.State = state;
                entries.Add(node.Entry);
            }
        });

        if (entries.Count == 0)
            throw new InvalidOperationException($"{entity.GetType().Name} is already tracked by this DbContext.");
        return entries;
    }

    private async Task<int> SaveAndDetachAsync(List<EntityEntry> entries, CancellationToken ct)
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
            foreach (var entry in entries)
                entry.State = EntityState.Detached;
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

        public Task<EntityResult> CommitAsync(CancellationToken ct = default) => EntityResult.RunAsync(() => _inner.CommitAsync(ct));
        public Task<EntityResult> RollbackAsync(CancellationToken ct = default) => EntityResult.RunAsync(() => _inner.RollbackAsync(ct));
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}

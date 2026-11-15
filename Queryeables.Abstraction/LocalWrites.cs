using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables.Abstraction;

/// <summary>
/// The writes made by this application, reported as soon as they are done (after the commit of their transaction): the
/// caches apply them at once, without waiting for the database's notification (which comes later, or never to its author
/// on a remote client). Fed by <see cref="LocalWritesDecorator"/>; registered as a singleton by <c>UseQueryeables</c>.
/// </summary>
public sealed class LocalWrites
{
    private readonly ConcurrentDictionary<Type, Delegate> _handlers = new();

    /// <summary>Calls <paramref name="handler"/> for each write of a <typeparamref name="TEntity"/>; dispose to stop.</summary>
    public IDisposable Subscribe<TEntity>(Action<TEntity, ChangeType> handler) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers.AddOrUpdate(typeof(TEntity), handler, (_, existing) => Delegate.Combine(existing, handler));
        return new Subscription(() => _handlers.AddOrUpdate(typeof(TEntity), _ => null!,
            (_, existing) => Delegate.Remove(existing, handler)!));
    }

    /// <summary>Reports that <paramref name="entity"/> was written.</summary>
    public void Report<TEntity>(TEntity entity, ChangeType type) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (_handlers.TryGetValue(typeof(TEntity), out var handler) && handler is Action<TEntity, ChangeType> typed)
            typed(entity, type);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                dispose();
        }
    }
}

/// <summary>
/// Reports to <see cref="LocalWrites"/> each entity this store inserts, updates or deletes: at once outside a transaction,
/// at the commit inside one (nothing on a rollback). The writes of many rows at once (<c>UpdateWhere</c>,
/// <c>DeleteWhere</c>) are not reported: the database's notifications bring them. Everything else goes to the inner store.
/// </summary>
public sealed class LocalWritesDecorator(IEntityStore inner, LocalWrites writes) : IEntityStore
{
    private readonly IEntityStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly LocalWrites _writes = writes ?? throw new ArgumentNullException(nameof(writes));
    private List<Action>? _pending;              // the writes of the open transaction

    private void Report<TEntity>(TEntity entity, ChangeType type) where TEntity : class
    {
        if (_pending is not null)
            _pending.Add(() => _writes.Report(entity, type));
        else
            _writes.Report(entity, type);
    }

    // ---------- Writes ----------

    public async Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
    {
        await _inner.InsertAsync(entity, ct);
        Report(entity, ChangeType.Insert);
    }

    public async Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
    {
        var updated = await _inner.UpdateAsync(entity, ct);
        if (updated)
            Report(entity, ChangeType.Update);
        return updated;
    }

    public async Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
    {
        var deleted = await _inner.DeleteAsync(entity, ct);
        if (deleted)
            Report(entity, ChangeType.Delete);
        return deleted;
    }

    public async Task<bool> PatchAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity original, TEntity modified,
        CancellationToken ct = default) where TEntity : class
    {
        var patched = await _inner.PatchAsync(original, modified, ct);
        if (patched)
            Report(modified, ChangeType.Update);       // holds the stored row's columns after the patch
        return patched;
    }

    public Task<int> UpdateWhereAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(Expression<Func<TEntity, bool>> predicate,
        EntityUpdate<TEntity> update, CancellationToken ct = default) where TEntity : class
        => _inner.UpdateWhereAsync(predicate, update, ct);

    public Task<int> DeleteWhereAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(Expression<Func<TEntity, bool>> predicate,
        CancellationToken ct = default) where TEntity : class
        => _inner.DeleteWhereAsync(predicate, ct);

    // ---------- Transactions ----------

    // Not async: what the inner store sets in the async flow of its transaction (the offline mode does) reaches the caller.
    public Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        return Wrap(_inner.BeginTransactionAsync(ct));

        async Task<IEntityTransaction> Wrap(Task<IEntityTransaction> begun)
        {
            var transaction = await begun;
            var pending = new List<Action>();
            _pending = pending;
            return new Transaction(this, transaction, pending);
        }
    }

    private sealed class Transaction(LocalWritesDecorator store, IEntityTransaction inner, List<Action> pending) : IEntityTransaction
    {
        public async Task<EntityResult> CommitAsync(CancellationToken ct = default)
        {
            var result = await inner.CommitAsync(ct);
            End();
            if (result.Successful)
            {
                foreach (var report in pending)
                    report();
            }
            pending.Clear();
            return result;
        }

        public async Task<EntityResult> RollbackAsync(CancellationToken ct = default)
        {
            var result = await inner.RollbackAsync(ct);
            End();
            pending.Clear();
            return result;
        }

        private void End()
        {
            if (ReferenceEquals(store._pending, pending))
                store._pending = null;
        }

        public async ValueTask DisposeAsync()
        {
            End();
            pending.Clear();
            await inner.DisposeAsync();
        }
    }

    // ---------- Reads: the inner store's ----------

    public Task<IReadOnlyList<TEntity>> GetAllAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(CancellationToken ct = default)
        where TEntity : class
        => _inner.GetAllAsync<TEntity>(ct);

    public Task<IReadOnlyList<TEntity>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => _inner.SelectAsync(predicate, ct);

    public Task<TEntity?> FirstOrDefaultAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => _inner.FirstOrDefaultAsync(predicate, ct);

    public Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize, CancellationToken ct = default) where TEntity : class
        => _inner.SelectPagedAsync(predicate, pageNumber, pageSize, ct);

    public bool SortsInStore => _inner.SortsInStore;

    public Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize, Sort<TEntity> sort, CancellationToken ct = default)
        where TEntity : class
        => _inner.SelectPagedAsync(predicate, pageNumber, pageSize, sort, ct);

    public Task<IReadOnlyList<TResult>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TResult>(
        Expression<Func<TEntity, bool>> predicate, Expression<Func<TEntity, TResult>> selector, CancellationToken ct = default)
        where TEntity : class
        => _inner.SelectAsync(predicate, selector, ct);

    public Task<long> CountAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(Expression<Func<TEntity, bool>> predicate,
        CancellationToken ct = default) where TEntity : class
        => _inner.CountAsync(predicate, ct);

    public Task<IReadOnlyList<TEntity>> SelectChangesAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(DateTime sinceUtc,
        CancellationToken ct = default) where TEntity : class
        => _inner.SelectChangesAsync<TEntity>(sinceUtc, ct);

    public Task<IReadOnlyList<TEntity>> SelectChangesAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(DateTime sinceUtc,
        Expression<Func<TEntity, bool>> predicate, int maxRows, CancellationToken ct = default) where TEntity : class
        => _inner.SelectChangesAsync(sinceUtc, predicate, maxRows, ct);

    public bool AggregatesInStore => _inner.AggregatesInStore;

    public Task<IReadOnlyList<AggregateRow>> AggregateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, Aggregation<TEntity> aggregation, CancellationToken ct = default) where TEntity : class
        => _inner.AggregateAsync(predicate, aggregation, ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    public void Dispose() => _inner.Dispose();
}

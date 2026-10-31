using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables.Remote.Offline;

/// <summary>
/// The store factory of an application that may work offline: remote stores while <see cref="OfflineMode.Mode"/> is
/// online, stores on the local copy while it is offline. Each store keeps the mode it was created in: services and
/// caches create one per call, so they follow the switch.
/// </summary>
public sealed class OfflineStoreFactory(IEntityStoreFactory online, OfflineMode mode) : IEntityStoreFactory
{
    private readonly IEntityStoreFactory _online = online ?? throw new ArgumentNullException(nameof(online));
    private readonly OfflineMode _mode = mode ?? throw new ArgumentNullException(nameof(mode));

    public IEntityStore Create() => _mode.Mode == ConnectionMode.Offline
        ? new OfflineEntityStore(_mode)
        : new WatchedEntityStore(_online.Create(), _mode);

    /// <summary>Stays true offline: the services keep sending a parent with its children, and the local copy saves them together.</summary>
    public bool IsRemote => _online.IsRemote;
}

/// <summary>A remote store that reports a lost network to <see cref="OfflineMode"/>, and otherwise changes nothing.</summary>
internal sealed class WatchedEntityStore(IEntityStore inner, OfflineMode mode) : IEntityStore
{
    public Task<IReadOnlyList<TEntity>> GetAllAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(CancellationToken ct = default)
        where TEntity : class
        => Watch(inner.GetAllAsync<TEntity>(ct), ct);

    public Task<IReadOnlyList<TEntity>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => Watch(inner.SelectAsync(predicate, ct), ct);

    public Task<TEntity?> FirstOrDefaultAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => Watch(inner.FirstOrDefaultAsync(predicate, ct), ct);

    public Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize, CancellationToken ct = default) where TEntity : class
        => Watch(inner.SelectPagedAsync(predicate, pageNumber, pageSize, ct), ct);

    public Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize, Sort<TEntity> sort, CancellationToken ct = default)
        where TEntity : class
        => Watch(inner.SelectPagedAsync(predicate, pageNumber, pageSize, sort, ct), ct);

    public Task<IReadOnlyList<TResult>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TResult>(
        Expression<Func<TEntity, bool>> predicate, Expression<Func<TEntity, TResult>> selector, CancellationToken ct = default)
        where TEntity : class
        => Watch(inner.SelectAsync(predicate, selector, ct), ct);

    public Task<long> CountAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => Watch(inner.CountAsync(predicate, ct), ct);

    public Task<IReadOnlyList<TEntity>> SelectChangesAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        DateTime sinceUtc, CancellationToken ct = default) where TEntity : class
        => Watch(inner.SelectChangesAsync<TEntity>(sinceUtc, ct), ct);

    public async Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
        => await Watch(Done(inner.InsertAsync(entity, ct)), ct);

    public Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
        => Watch(inner.UpdateAsync(entity, ct), ct);

    public Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
        => Watch(inner.DeleteAsync(entity, ct), ct);

    public async Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => new WatchedTransaction(await inner.BeginTransactionAsync(ct), mode);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    public void Dispose() => inner.Dispose();

    private async Task<T> Watch<T>(Task<T> call, CancellationToken ct)
    {
        try
        {
            return await call;
        }
        catch (Exception ex) when (OfflineMode.IsTransportFailure(ex, ct))
        {
            mode.ReportUnreachable();
            throw;
        }
    }

    private static async Task<bool> Done(Task call)
    {
        await call;
        return true;
    }

    /// <summary>A remote commit returns its failure: an unexplained one may be a lost network, so the server is checked.</summary>
    private sealed class WatchedTransaction(IEntityTransaction inner, OfflineMode mode) : IEntityTransaction
    {
        public async Task<EntityResult> CommitAsync(CancellationToken ct = default)
        {
            var result = await inner.CommitAsync(ct);
            if (result.ErrorCode == EntityErrorCode.Unknown)
                _ = mode.CheckServerAsync(CancellationToken.None);
            return result;
        }

        public Task<EntityResult> RollbackAsync(CancellationToken ct = default) => inner.RollbackAsync(ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

/// <summary>
/// A store on the local copy. Reads are answered in memory; each write changes the copy, is queued for the server and
/// notifies the caches. A parent written with an entity graph is saved with its children, as the server would, and queued
/// as one write: the server saves the children again when it receives it.
/// </summary>
internal sealed class OfflineEntityStore(OfflineMode mode) : IEntityStore
{
    private IEntityTransaction? _transaction;

    // ---------- Read ----------

    public async Task<IReadOnlyList<TEntity>> GetAllAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(CancellationToken ct = default)
        where TEntity : class
        => await ReadAsync<TEntity>(null, ct);

    public async Task<IReadOnlyList<TEntity>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return await ReadAsync(predicate, ct);
    }

    public async Task<TEntity?> FirstOrDefaultAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => (await SelectAsync(predicate, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize, CancellationToken ct = default) where TEntity : class
    {
        Paging.Validate(pageNumber, pageSize);
        return (await SelectAsync(predicate, ct)).Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
    }

    public Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_transaction is not null)
            throw new InvalidOperationException("A transaction is already in progress on this store.");
        return BeginAsync(ct);
    }

    // ---------- Write ----------

    public async Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
        => await WriteAsync(BatchOperationKind.Insert, entity, ct);

    public Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
        => WriteAsync(BatchOperationKind.Update, entity, ct);

    public Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
        => WriteAsync(BatchOperationKind.Delete, entity, ct);

    public async ValueTask DisposeAsync()
    {
        if (_transaction is { } transaction)
        {
            _transaction = null;
            await transaction.DisposeAsync();     // rolls back unless committed
        }
    }

    public void Dispose()
    {
        // Synchronous disposal cannot wait (WebAssembly): the rollback finishes on its own.
        if (_transaction is { } transaction)
        {
            _transaction = null;
            _ = transaction.DisposeAsync().AsTask();
        }
    }

    // ---------- Internals ----------

    private async Task<IEntityTransaction> BeginAsync(CancellationToken ct)
    {
        _transaction = await mode.BeginTransactionAsync(ct);
        return _transaction;
    }

    private async Task<IReadOnlyList<TEntity>> ReadAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>>? predicate, CancellationToken ct) where TEntity : class
    {
        var type = mode.Require(typeof(TEntity));
        var match = predicate?.Compile();
        await mode.Gate.WaitAsync(ct);
        try
        {
            var rows = type.CopyRows().Cast<TEntity>();
            return (match is null ? rows : rows.Where(match)).ToList();
        }
        finally
        {
            mode.Gate.Release();
        }
    }

    private async Task<bool> WriteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        BatchOperationKind kind, TEntity entity, CancellationToken ct) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var type = mode.Require(typeof(TEntity));
        var graph = mode.GraphOf<TEntity>();
        bool written;
        bool saved;

        await mode.Gate.WaitAsync(ct);
        try
        {
            var replica = new ReplicaStore(mode);
            if (graph is null)
            {
                written = replica.Apply(type, kind, entity);
            }
            else
            {
                // The parent and its children, all or nothing, as the server's graph would save them.
                var before = mode.SnapshotTypes();
                try
                {
                    written = await replica.RunGraphAsync(graph, kind, entity, ct);
                }
                catch
                {
                    mode.RestoreTypes(before);
                    throw;
                }

                if (!written)
                    mode.RestoreTypes(before);     // children may have gone before the parent was found missing
            }

            if (!written)
            {
                if (kind == BatchOperationKind.Insert)
                    throw new EntityStoreException(EntityErrorCode.InvalidRequest,
                        $"{typeof(TEntity).Name} has a key: its graph updates it, and it does not exist. Insert it with a default key.");
                return false;
            }

            replica.Publish();
            saved = await mode.QueueAsync(type, kind, type.KeyTextOf(entity), mode.Serializer.Serialize(entity), ct);
        }
        finally
        {
            mode.Gate.Release();
        }

        if (saved)
            mode.AfterWrite();
        return true;
    }
}

/// <summary>
/// Writes straight into the local copy, without queueing: the store an entity graph runs on offline. Children get
/// temporary keys like their parent; changes are published once the whole graph succeeded.
/// </summary>
internal sealed class ReplicaStore(OfflineMode mode) : IEntityStore
{
    private readonly List<(OfflineEntityType Type, ChangeType Change, string Key)> _changes = [];

    public bool Apply(OfflineEntityType type, BatchOperationKind kind, object entity)
    {
        if (!type.Apply(kind, entity))
            return false;
        mode.MarkDirty(type);
        _changes.Add((type, kind switch
        {
            BatchOperationKind.Insert => ChangeType.Insert,
            BatchOperationKind.Update => ChangeType.Update,
            _ => ChangeType.Delete,
        }, type.KeyTextOf(entity)));
        return true;
    }

    public async Task<bool> RunGraphAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        IEntityGraph<TEntity> graph, BatchOperationKind kind, TEntity entity, CancellationToken ct) where TEntity : class
    {
        try
        {
            if (kind == BatchOperationKind.Delete)
                await graph.RemoveAsync(this, entity, ct);
            else
                await graph.SaveAsync(this, entity, ct);     // insert or update, by the key, as on the server
            return true;
        }
        catch (EntityStoreException ex) when (ex.ErrorCode == EntityErrorCode.NotFound)
        {
            return false;
        }
    }

    public void Publish()
    {
        foreach (var (type, change, key) in _changes)
            mode.Notify(type, change, key);
        _changes.Clear();
    }

    // ---------- IEntityStore, for the graph ----------

    public Task<IReadOnlyList<TEntity>> GetAllAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(CancellationToken ct = default)
        where TEntity : class
        => Task.FromResult<IReadOnlyList<TEntity>>(mode.Require(typeof(TEntity)).CopyRows().Cast<TEntity>().ToList());

    public Task<IReadOnlyList<TEntity>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        var match = predicate.Compile();
        return Task.FromResult<IReadOnlyList<TEntity>>(
            mode.Require(typeof(TEntity)).CopyRows().Cast<TEntity>().Where(match).ToList());
    }

    public async Task<TEntity?> FirstOrDefaultAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => (await SelectAsync(predicate, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize, CancellationToken ct = default) where TEntity : class
    {
        Paging.Validate(pageNumber, pageSize);
        return (await SelectAsync(predicate, ct)).Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
    }

    public Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
    {
        Apply(mode.Require(typeof(TEntity)), BatchOperationKind.Insert, entity);
        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
        => Task.FromResult(Apply(mode.Require(typeof(TEntity)), BatchOperationKind.Update, entity));

    public Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
        => Task.FromResult(Apply(mode.Require(typeof(TEntity)), BatchOperationKind.Delete, entity));

    /// <summary>Already inside the caller's write: nothing more to open.</summary>
    public Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => Task.FromResult<IEntityTransaction>(NoTransaction.Instance);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
    }

    private sealed class NoTransaction : IEntityTransaction
    {
        public static readonly NoTransaction Instance = new();

        public Task<EntityResult> CommitAsync(CancellationToken ct = default) => Task.FromResult(EntityResult.Success());

        public Task<EntityResult> RollbackAsync(CancellationToken ct = default) => Task.FromResult(EntityResult.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Remote;

/// <summary>
/// Client-side <see cref="IEntityStore"/> on top of any transport (<see cref="IEntityRemote"/>): the same code, caches and
/// transactions remotely as locally.
/// <para>
/// <b>Predicates:</b> comparisons, booleans, <c>list.Contains(e.Prop)</c>, StartsWith/EndsWith/Contains on strings,
/// all combined with AND or all with OR. Anything else throws <see cref="NotSupportedException"/> before any call.
/// </para>
/// <para>
/// <b>Transactions:</b> during a transaction, writes are queued and sent in a single call
/// on <see cref="IEntityTransaction.CommitAsync"/>, executed in a server-side transaction: everything is applied,
/// or nothing. Meanwhile, UpdateAsync and DeleteAsync return true, and an insert only gets its key on Commit;
/// reads do not see the pending writes yet.
/// </para>
/// <para>
/// <b>Errors:</b> reads and writes throw <see cref="EntityStoreException"/>; a transaction commit returns a
/// <see cref="DbResult"/>. Both carry the kind of error (permissions, validation, conflict...).
/// </para>
/// </summary>
/// <param name="remote">The transport.</param>
/// <param name="serializer">The entity serializer, the same as the server's.</param>
/// <param name="session">Notification sessions: the session id goes with every write, so that this client ignores its own changes.</param>
public sealed class RemoteEntityStore(IEntityRemote remote, IEntitySerializer serializer, IDbSession? session = null) : IEntityStore
{
    private readonly IEntityRemote _remote = remote ?? throw new ArgumentNullException(nameof(remote));
    private readonly IEntitySerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    private RemoteTransaction? _transaction;

    /// <summary>A factory of remote stores (<see cref="IEntityStoreFactory.IsRemote"/> is true).</summary>
    public static IEntityStoreFactory Factory(IEntityRemote remote, IEntitySerializer serializer, IDbSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(serializer);
        return new EntityStoreFactory(() => new RemoteEntityStore(remote, serializer, session), isRemote: true);
    }

    // ---------- Read ----------

    public async Task<IReadOnlyList<TEntity>> GetAllAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(CancellationToken ct = default) where TEntity : class
        => DeserializeMany<TEntity>(await CallAsync(_remote.GetAllAsync(Name<TEntity>(), ct)));

    public async Task<IReadOnlyList<TEntity>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        var (conditions, any) = Filters.FromPredicate(predicate);
        return DeserializeMany<TEntity>(await CallAsync(_remote.SelectAsync(Name<TEntity>(), conditions, any, ct)));
    }

    public async Task<TEntity?> FirstOrDefaultAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => (await SelectPagedAsync(predicate, 1, 1, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize,
        CancellationToken ct = default) where TEntity : class
    {
        Paging.Validate(pageNumber, pageSize);
        var (conditions, any) = Filters.FromPredicate(predicate);
        return DeserializeMany<TEntity>(
            await CallAsync(_remote.SelectPagedAsync(Name<TEntity>(), conditions, any, pageNumber, pageSize, ct)));
    }

    /// <summary>Rows modified since <paramref name="sinceUtc"/>, soft-deleted ones included.</summary>
    public async Task<IReadOnlyList<TEntity>> SelectChangesAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(DateTime sinceUtc, CancellationToken ct = default)
        where TEntity : class
        => DeserializeMany<TEntity>(
            await CallAsync(_remote.GetChangesAsync(Name<TEntity>(), DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc), ct)));

    // ---------- Write ----------

    public async Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Enqueue(BatchOperationKind.Insert, entity))
            return;

        var data = await CallAsync(_remote.InsertAsync(Name<TEntity>(), _serializer.Serialize(entity), ConnectionId<TEntity>(), ct));
        ApplyResult(data, entity); // generated key and version
    }

    public async Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Enqueue(BatchOperationKind.Update, entity))
            return true;

        var data = await CallAsync(_remote.UpdateAsync(Name<TEntity>(), _serializer.Serialize(entity), ConnectionId<TEntity>(), ct));
        return ApplyResult(data, entity); // empty data: row not found
    }

    public async Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Enqueue(BatchOperationKind.Delete, entity))
            return true;

        var data = await CallAsync(_remote.DeleteAsync(Name<TEntity>(), _serializer.Serialize(entity), ConnectionId<TEntity>(), ct));
        return data is { Length: > 0 } && _serializer.Deserialize<bool>(data);
    }

    // ---------- Transactions ----------

    public Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_transaction is not null)
            throw new InvalidOperationException("A transaction is already in progress on this store.");

        _transaction = new RemoteTransaction(this);
        return Task.FromResult<IEntityTransaction>(_transaction);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _transaction = null; // pending writes discarded

    /// <summary>Queues the write when a transaction is in progress.</summary>
    private bool Enqueue<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(BatchOperationKind kind, TEntity entity) where TEntity : class
    {
        if (_transaction is null)
            return false;

        var operation = new BatchOperation(Name<TEntity>(), kind, _serializer.Serialize(entity), ConnectionId<TEntity>());
        Action<byte[]> applyResult = kind == BatchOperationKind.Delete
            ? _ => { }
            : data => ApplyResult(data, entity);
        _transaction.Add(operation, applyResult);
        return true;
    }

    private async Task<DbResult> CommitAsync(RemoteTransaction transaction, CancellationToken ct)
    {
        if (!ReferenceEquals(_transaction, transaction))
            return DbResult.Failure(EntityErrorCode.InvalidRequest, "This transaction is no longer active.");
        _transaction = null;

        if (transaction.Operations.Count == 0)
            return DbResult.Success();

        var operations = transaction.Operations.Select(o => o.Operation).ToArray();
        RemoteResult response;
        try
        {
            response = await _remote.ExecuteBatchAsync(operations, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DbResult.FromException(ex);
        }

        if (!response.Successful)
            return response.ToDbResult();

        var results = response.Data is { Length: > 0 }
            ? _serializer.Deserialize<byte[][]>(response.Data) ?? []
            : [];

        // Generated keys and new versions copied back into the original entities.
        for (var i = 0; i < Math.Min(results.Length, transaction.Operations.Count); i++)
            transaction.Operations[i].ApplyResult(results[i]);
        return DbResult.Success();
    }

    private void Abandon(RemoteTransaction transaction)
    {
        if (ReferenceEquals(_transaction, transaction))
            _transaction = null;
    }

    private sealed class RemoteTransaction(RemoteEntityStore store) : IEntityTransaction
    {
        public List<(BatchOperation Operation, Action<byte[]> ApplyResult)> Operations { get; } = [];
        private bool _done;

        public void Add(BatchOperation operation, Action<byte[]> applyResult) => Operations.Add((operation, applyResult));

        public Task<DbResult> CommitAsync(CancellationToken ct = default)
        {
            if (_done)
                return Task.FromResult(DbResult.Failure(EntityErrorCode.InvalidRequest, "The transaction is already finished."));
            _done = true;
            return store.CommitAsync(this, ct);
        }

        public Task<DbResult> RollbackAsync(CancellationToken ct = default)
        {
            _done = true;
            store.Abandon(this); // nothing was sent: forgetting the writes is enough
            return Task.FromResult(DbResult.Success());
        }

        public ValueTask DisposeAsync()
        {
            if (!_done)
                store.Abandon(this);
            _done = true;
            return ValueTask.CompletedTask;
        }
    }

    // ---------- Internals ----------

    private static async Task<byte[]?> CallAsync(Task<RemoteResult> call) => (await call).GetDataOrThrow();

    /// <summary>Name under which the server registers the type (see <see cref="EntityName"/>).</summary>
    private static string Name<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>() => EntityName.Of<TEntity>();

    private string? ConnectionId<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>()
        => session is not null && session.TryGetSession(typeof(TEntity), out var current) ? current.session : null;

    private IReadOnlyList<TEntity> DeserializeMany<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(byte[]? data)
        => data is { Length: > 0 } ? _serializer.Deserialize<TEntity[]>(data) ?? [] : [];

    /// <summary>Copies the entity returned by the server into the caller's one. False when the server returned nothing.</summary>
    private bool ApplyResult<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(byte[]? data, TEntity target) where TEntity : class
    {
        if (data is not { Length: > 0 })
            return false;

        var source = _serializer.Deserialize<TEntity>(data);
        if (source is null)
            return false;

        foreach (var property in typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                property.SetValue(target, property.GetValue(source));
        }
        return true;
    }
}

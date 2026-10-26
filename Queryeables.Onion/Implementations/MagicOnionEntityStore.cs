using MagicOnion;
using MessagePack;
using Queryeables.Abstraction;
using System.Linq.Expressions;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Onion;

/// <summary>
/// Client-side <see cref="IEntityStore"/>, on top of the MagicOnion service. Allows using
/// <see cref="EntityCache{TEntity, TKey}"/> and transactions remotely just like locally.
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
/// <see cref="DBResult"/>. Both carry the kind of error (permissions, validation, conflict...).
/// </para>
/// </summary>
/// <example>
/// <code>
/// var layer = MagicOnionClient.Create&lt;IMagicOnionLayer&gt;(channel);
/// var cache = new EntityCache&lt;Customer, long&gt;(
///     () => new MagicOnionEntityStore(layer, session), c => c.Id, monitor, RecordKey.Parse&lt;long&gt;);
/// </code>
/// </example>
public sealed class MagicOnionEntityStore(
    IMagicOnionLayer layer,
    IDbSession? session = null,
    MessagePackSerializerOptions? options = null) : IEntityStore
{
    private RemoteTransaction? _transaction;

    // ---------- Read ----------

    public async Task<IReadOnlyList<TEntity>> GetAllAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(CancellationToken ct = default) where TEntity : class
        => DeserializeMany<TEntity>(await CallAsync(l => l.GetAllAsync(Name<TEntity>()), ct), ct);

    public async Task<IReadOnlyList<TEntity>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        var (conditions, any) = Filters.FromPredicate(predicate);
        return DeserializeMany<TEntity>(await CallAsync(l => l.SelectAsync(Name<TEntity>(), conditions, any), ct), ct);
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
            await CallAsync(l => l.SelectPagedAsync(Name<TEntity>(), conditions, any, pageNumber, pageSize), ct), ct);
    }

    /// <summary>Rows modified since <paramref name="sinceUtc"/>, soft-deleted ones included.</summary>
    public async Task<IReadOnlyList<TEntity>> SelectChangesAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(DateTime sinceUtc, CancellationToken ct = default)
        where TEntity : class
        => DeserializeMany<TEntity>(
            await CallAsync(l => l.GetChangesAsync(Name<TEntity>(), DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc)), ct), ct);

    // ---------- Write ----------

    public async Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Enqueue(BatchOperationKind.Insert, entity, ct))
            return;

        var data = await CallAsync(l => l.InsertAsync(Name<TEntity>(), Serialize(entity, ct), ConnectionId<TEntity>()), ct);
        ApplyResult(data, entity, ct); // generated key and version
    }

    public async Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Enqueue(BatchOperationKind.Update, entity, ct))
            return true;

        var data = await CallAsync(l => l.UpdateAsync(Name<TEntity>(), Serialize(entity, ct), ConnectionId<TEntity>()), ct);
        return ApplyResult(data, entity, ct); // empty data: row not found
    }

    public async Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Enqueue(BatchOperationKind.Delete, entity, ct))
            return true;

        var data = await CallAsync(l => l.DeleteAsync(Name<TEntity>(), Serialize(entity, ct), ConnectionId<TEntity>()), ct);
        return data is { Length: > 0 } && MessagePackSerializer.Deserialize<bool>(data, options, ct);
    }

    // ---------- Transactions ----------

    public Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_transaction is not null)
            throw new InvalidOperationException("Une transaction est déjà en cours sur ce store.");

        _transaction = new RemoteTransaction(this);
        return Task.FromResult<IEntityTransaction>(_transaction);
    }

    public ValueTask DisposeAsync()
    {
        _transaction = null; // pending writes discarded
        return ValueTask.CompletedTask;
    }

    /// <summary>Queues the write when a transaction is in progress.</summary>
    private bool Enqueue<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(BatchOperationKind kind, TEntity entity, CancellationToken ct) where TEntity : class
    {
        if (_transaction is null)
            return false;

        var operation = new BatchOperation(Name<TEntity>(), kind, Serialize(entity, ct), ConnectionId<TEntity>());
        Action<byte[]> applyResult = kind == BatchOperationKind.Delete
            ? _ => { }
            : data => ApplyResult(data, entity, CancellationToken.None);
        _transaction.Add(operation, applyResult);
        return true;
    }

    private async Task<DBResult> CommitAsync(RemoteTransaction transaction, CancellationToken ct)
    {
        if (!ReferenceEquals(_transaction, transaction))
            return DBResult.Failure(EntityErrorCode.InvalidRequest, "Cette transaction n'est plus active.");
        _transaction = null;

        if (transaction.Operations.Count == 0)
            return DBResult.Success();

        var operations = transaction.Operations.Select(o => o.Operation).ToArray();
        DBResultMessage response;
        try
        {
            response = await layer.WithCancellationToken(ct).ExecuteBatchAsync(operations);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DBResult.FromException(ex);
        }

        if (!response.Successful)
            return response.ToDBResult();

        var results = response.Data is { Length: > 0 }
            ? MessagePackSerializer.Deserialize<byte[][]>(response.Data, options, ct) ?? []
            : [];

        // Generated keys and new versions copied back into the original entities.
        for (var i = 0; i < Math.Min(results.Length, transaction.Operations.Count); i++)
            transaction.Operations[i].ApplyResult(results[i]);
        return DBResult.Success();
    }

    private void Abandon(RemoteTransaction transaction)
    {
        if (ReferenceEquals(_transaction, transaction))
            _transaction = null;
    }

    private sealed class RemoteTransaction(MagicOnionEntityStore store) : IEntityTransaction
    {
        public List<(BatchOperation Operation, Action<byte[]> ApplyResult)> Operations { get; } = [];
        private bool _done;

        public void Add(BatchOperation operation, Action<byte[]> applyResult) => Operations.Add((operation, applyResult));

        public Task<DBResult> CommitAsync(CancellationToken ct = default)
        {
            if (_done)
                return Task.FromResult(DBResult.Failure(EntityErrorCode.InvalidRequest, "La transaction est déjà terminée."));
            _done = true;
            return store.CommitAsync(this, ct);
        }

        public Task<DBResult> RollbackAsync(CancellationToken ct = default)
        {
            _done = true;
            store.Abandon(this); // nothing was sent: forgetting the writes is enough
            return Task.FromResult(DBResult.Success());
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

    private async Task<byte[]?> CallAsync(Func<IMagicOnionLayer, UnaryResult<DBResultMessage>> call, CancellationToken ct)
        => (await call(layer.WithCancellationToken(ct))).GetDataOrThrow();

    /// <summary>Name under which the server registers the type (see <see cref="EntityName"/>).</summary>
    private static string Name<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>() => EntityName.Of<TEntity>();

    private string? ConnectionId<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>()
        => session is not null && session.TryGetSession(typeof(TEntity), out var current) ? current.session : null;

    private byte[] Serialize<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct)
        => MessagePackSerializer.Serialize(entity, options, ct);

    private IReadOnlyList<TEntity> DeserializeMany<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(byte[]? data, CancellationToken ct)
        => data is { Length: > 0 }
            ? MessagePackSerializer.Deserialize<TEntity[]>(data, options, ct) ?? []
            : [];

    /// <summary>Copies the entity returned by the server into the caller's one. False when the server returned nothing.</summary>
    private bool ApplyResult<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(byte[]? data, TEntity target, CancellationToken ct) where TEntity : class
    {
        if (data is not { Length: > 0 })
            return false;

        var source = MessagePackSerializer.Deserialize<TEntity>(data, options, ct);
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

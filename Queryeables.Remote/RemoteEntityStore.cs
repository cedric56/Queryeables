using Queryeables.Abstraction;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Remote;

/// <summary>
/// Client-side <see cref="IEntityStore"/> on top of any transport (<see cref="IEntityTransport"/>): the same code, caches and
/// transactions remotely as locally.
/// <para>
/// <b>Predicates:</b> comparisons, booleans, <c>list.Contains(e.Prop)</c>, StartsWith/EndsWith/Contains on strings,
/// combined with &amp;&amp;, || and ! in any nesting. Anything else throws <see cref="NotSupportedException"/> before any call.
/// </para>
/// <para>
/// <b>Transactions:</b> during a transaction, writes are queued and sent in a single call
/// on <see cref="IEntityTransaction.CommitAsync"/>, executed in a server-side transaction: everything is applied,
/// or nothing. Meanwhile, UpdateAsync and DeleteAsync return true, and an insert only gets its key on Commit;
/// reads do not see the pending writes yet.
/// </para>
/// <para>
/// <b>Errors:</b> reads and writes throw <see cref="EntityStoreException"/>; a transaction commit returns a
/// <see cref="EntityResult"/>. Both carry the kind of error (permissions, validation, conflict...).
/// </para>
/// </summary>
/// <param name="remote">The transport.</param>
/// <param name="serializer">The entity serializer, the same as the server's.</param>
/// <param name="session">Notification sessions: the session id goes with every write, so that this client ignores its own changes.</param>
public sealed class RemoteEntityStore(IEntityTransport remote, IEntitySerializer serializer, IChangeSession? session = null) : IEntityStore
{
    private readonly IEntityTransport _remote = remote ?? throw new ArgumentNullException(nameof(remote));
    private readonly IEntitySerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    private RemoteTransaction? _transaction;

    /// <summary>Sorted pages are sorted by the server, which bounds them: only the page crosses the network.</summary>
    public bool SortsInStore => true;

    /// <summary>A factory of remote stores (<see cref="IEntityStoreFactory.IsRemote"/> is true).</summary>
    public static IEntityStoreFactory Factory(IEntityTransport remote, IEntitySerializer serializer, IChangeSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(serializer);
        return new EntityStoreFactory(() => new RemoteEntityStore(remote, serializer, session), isRemote: true);
    }

    // ---------- Read ----------

    public async Task<IReadOnlyList<TEntity>> GetAllAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(CancellationToken ct = default) where TEntity : class
        => DeserializeMany<TEntity>(await CallAsync("get_all", Name<TEntity>(), () => _remote.GetAllAsync(Name<TEntity>(), ct)));

    public async Task<IReadOnlyList<TEntity>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        var (conditions, any) = Filters.FromPredicate(predicate);
        return DeserializeMany<TEntity>(await CallAsync("select", Name<TEntity>(), () => _remote.SelectAsync(Name<TEntity>(), conditions, any, ct)));
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
            await CallAsync("select_paged", Name<TEntity>(), () => _remote.SelectPagedAsync(Name<TEntity>(), conditions, any, pageNumber, pageSize, ct)));
    }

    /// <summary>Sorted and paged by the server.</summary>
    public async Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize, Sort<TEntity> sort,
        CancellationToken ct = default) where TEntity : class
    {
        Paging.Validate(pageNumber, pageSize);
        ArgumentNullException.ThrowIfNull(sort);
        var (conditions, any) = Filters.FromPredicate(predicate);
        return DeserializeMany<TEntity>(await CallAsync("select_paged", Name<TEntity>(), () => _remote.SelectPagedAsync(Name<TEntity>(), conditions, any, pageNumber, pageSize, [.. sort.Keys], ct)));
    }

    /// <summary>Rows modified since <paramref name="sinceUtc"/>, soft-deleted ones included.</summary>
    public async Task<IReadOnlyList<TEntity>> SelectChangesAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(DateTime sinceUtc, CancellationToken ct = default)
        where TEntity : class
        => DeserializeMany<TEntity>(
            await CallAsync("get_changes", Name<TEntity>(), () => _remote.GetChangesAsync(Name<TEntity>(), DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc), ct)));

    /// <summary>
    /// The server's changes (already limited to the rows this user may read), then <paramref name="predicate"/> in memory:
    /// a predicate does not travel with a change request.
    /// </summary>
    public async Task<IReadOnlyList<TEntity>> SelectChangesAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(DateTime sinceUtc,
        Expression<Func<TEntity, bool>> predicate, int maxRows, CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var match = predicate.Compile();
        return [.. (await SelectChangesAsync<TEntity>(sinceUtc, ct)).Where(match).Take(maxRows)];
    }

    // ---------- Write ----------

    public async Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Enqueue(BatchOperationKind.Insert, entity))
            return;

        var sent = _serializer.Serialize(entity);
        var data = await CallAsync("insert", Name<TEntity>(), () => _remote.InsertAsync(Name<TEntity>(), sent, ConnectionId<TEntity>(), ct));
        ApplyResult(data, sent, entity); // generated key and version
    }

    public async Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Enqueue(BatchOperationKind.Update, entity))
            return true;

        var sent = _serializer.Serialize(entity);

        // A graph whose children this user partly left as read: the server leaves their rows alone.
        if (GraphChanges.Unchanged is { Count: > 0 } unchanged)
        {
            var request = new RemoteRequest { Kind = RemoteRequestKind.SaveGraph, Entity = sent, Members = [.. unchanged], ConnectionId = ConnectionId<TEntity>() };
            var (supported, saved) = await ExecuteAsync<TEntity>(request, ct);
            if (supported)
                return ApplyResult(saved, sent, entity);
        }

        var data = await CallAsync("update", Name<TEntity>(), () => _remote.UpdateAsync(Name<TEntity>(), sent, ConnectionId<TEntity>(), ct));
        return ApplyResult(data, sent, entity); // empty data: row not found
    }

    public async Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Enqueue(BatchOperationKind.Delete, entity))
            return true;

        var data = await CallAsync("delete", Name<TEntity>(), () => _remote.DeleteAsync(Name<TEntity>(), _serializer.Serialize(entity), ConnectionId<TEntity>(), ct));
        return data is { Length: > 0 } && _serializer.Deserialize<bool>(data);
    }

    // ---------- Done by the server (RemoteRequest); by this store against an older server ----------

    /// <summary>Counted by the server.</summary>
    public async Task<long> CountAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        var (conditions, any) = Filters.FromPredicate(predicate);
        var (supported, data) = await ExecuteAsync<TEntity>(new RemoteRequest { Kind = RemoteRequestKind.Count, Filter = conditions, Any = any }, ct);
        return supported ? _serializer.Deserialize<long>(data ?? []) : (await SelectAsync(predicate, ct)).Count;
    }

    /// <summary>The server computes them, within its own limits.</summary>
    public bool AggregatesInStore => true;

    /// <summary>Computed by the server: only the results travel.</summary>
    public async Task<IReadOnlyList<AggregateRow>> AggregateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, Aggregation<TEntity> aggregation, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(aggregation);
        var values = new List<FilterCondition>();
        if (aggregation.Key is { } key)
        {
            FilterLanguage.EncodeValue(key, values);
            values.Add(new FilterCondition(string.Empty, FilterOperator.GroupKey, null));
        }
        foreach (var value in aggregation.Values)
        {
            if (value.Value is { } read)
                FilterLanguage.EncodeValue(read, values);
            values.Add(new FilterCondition(value.Function.ToString(), FilterOperator.Aggregate, null));
        }

        var (conditions, any) = Filters.FromPredicate(predicate);
        var request = new RemoteRequest { Kind = RemoteRequestKind.Aggregate, Filter = conditions, Any = any, Values = [.. values] };
        var (supported, data) = await ExecuteAsync<TEntity>(request, ct);
        if (!supported)
        {
            if (aggregation.Projection() is not { } projection)
            {
                var count = await CountAsync(predicate, ct);
                return [new AggregateRow(null, count, [.. aggregation.Values.Select(_ => (object?)count)])];
            }
            return aggregation.Compute(await SelectAsync(predicate, projection, ct));
        }

        var rows = data is { Length: > 0 } ? _serializer.Deserialize<object?[][]>(data) ?? [] : [];
        return [.. rows.Select(row => new AggregateRow(
            aggregation.Key is { } groupKey ? Filters.ConvertTo(row[0], groupKey.ReturnType, "the key") : null,
            Convert.ToInt64(Filters.Plain(row[1]), System.Globalization.CultureInfo.InvariantCulture),
            [.. aggregation.Values.Select((v, i) => v.Function == AggregateFunction.Count
                ? Convert.ToInt64(Filters.Plain(row[2 + i]), System.Globalization.CultureInfo.InvariantCulture)
                : Filters.ConvertTo(row[2 + i], NullableOf(Aggregation<TEntity>.ResultType(v.Function, v.Type!)), v.Function.ToString()))]))];
    }

    /// <summary>The server sends only the properties the selector reads; the selector runs here.</summary>
    public async Task<IReadOnlyList<TResult>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TResult>(
        Expression<Func<TEntity, bool>> predicate, Expression<Func<TEntity, TResult>> selector, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(selector);
        var project = selector.Compile();
        if (EntityValues.ColumnsReadBy<TEntity>(selector) is { } columns)
        {
            var (conditions, any) = Filters.FromPredicate(predicate);
            var request = new RemoteRequest { Kind = RemoteRequestKind.Project, Filter = conditions, Any = any, Members = [.. columns.Select(c => c.Name)] };
            var (supported, data) = await ExecuteAsync<TEntity>(request, ct);
            if (supported)
                return [.. DeserializeMany<TEntity>(data).Select(project)];
        }
        return [.. (await SelectAsync(predicate, ct)).Select(project)];
    }

    /// <summary>Only the changed properties travel; the server writes them over the stored row.</summary>
    public async Task<bool> PatchAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity original, TEntity modified,
        CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        NotInTransaction("A patch");
        var changed = EntityValues.Changed(original, modified);
        var request = new RemoteRequest
        {
            Kind = RemoteRequestKind.Patch,
            Original = _serializer.Serialize(original),
            Entity = _serializer.Serialize(modified),
            Members = [.. changed.Select(c => c.Name)],
            ConnectionId = ConnectionId<TEntity>(),
        };
        var (supported, data) = await ExecuteAsync<TEntity>(request, ct);
        if (supported)
        {
            if (data is not { Length: > 0 } || _serializer.Deserialize<TEntity>(data) is not { } stored)
                return false;
            EntityPatch.CopyColumns(stored, modified);
            return true;
        }

        // An older server: the patch is merged here, and written as an update (its version keeps it safe).
        if (await FirstOrDefaultAsync(SyncMetadata.SameKey(modified), ct) is not { } row)
            return false;
        if (EntityPatch.Merge(row, original, modified, changed) && !await UpdateAsync(row, ct))
            return false;
        EntityPatch.CopyColumns(row, modified);
        return true;
    }

    /// <summary>Done by the server, in one call and one transaction.</summary>
    public async Task<int> UpdateWhereAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(Expression<Func<TEntity, bool>> predicate,
        EntityUpdate<TEntity> update, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(update);
        NotInTransaction("An update of many rows");
        var (conditions, any) = Filters.FromPredicate(predicate);
        var request = new RemoteRequest
        {
            Kind = RemoteRequestKind.UpdateWhere,
            Filter = conditions,
            Any = any,
            Values = [.. update.Values.Select(v => new FilterCondition(v.Property.Name, FilterOperator.Equal, FilterLanguage.Normalize(v.Value)))],
            ConnectionId = ConnectionId<TEntity>(),
        };
        var (supported, data) = await ExecuteAsync<TEntity>(request, ct);
        if (supported)
            return _serializer.Deserialize<int>(data ?? []);

        var rows = await SelectAsync(predicate, ct);
        var result = await this.UpdateManyAsync(rows.Select(r =>
        {
            update.Apply(r);
            return r;
        }), ct);
        result.ThrowIfFailed();
        return rows.Count;
    }

    /// <summary>Done by the server, in one call and one transaction.</summary>
    public async Task<int> DeleteWhereAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(Expression<Func<TEntity, bool>> predicate,
        CancellationToken ct = default) where TEntity : class
    {
        NotInTransaction("A deletion of many rows");
        var (conditions, any) = Filters.FromPredicate(predicate);
        var request = new RemoteRequest { Kind = RemoteRequestKind.DeleteWhere, Filter = conditions, Any = any, ConnectionId = ConnectionId<TEntity>() };
        var (supported, data) = await ExecuteAsync<TEntity>(request, ct);
        if (supported)
            return _serializer.Deserialize<int>(data ?? []);

        var rows = await SelectAsync(predicate, ct);
        (await this.DeleteManyAsync(rows, ct)).ThrowIfFailed();
        return rows.Count;
    }

    /// <summary>The answer of the server; not supported by an older server (or transport): the store does the work itself.</summary>
    private async Task<(bool Supported, byte[]? Data)> ExecuteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        RemoteRequest request, CancellationToken ct) where TEntity : class
    {
        var result = await RemoteDiagnostics.ClientAsync("execute_" + request.Kind.ToString().ToLowerInvariant(), Name<TEntity>(),
            () => _remote.ExecuteAsync(Name<TEntity>(), request, ct));
        return result is { Successful: false, ErrorCode: EntityErrorCode.NotSupported } ? (false, null) : (true, result.GetDataOrThrow());
    }

    private void NotInTransaction(string what)
    {
        if (_transaction is not null)
            throw new NotSupportedException($"{what} is done by the server at once: it cannot be part of a remote transaction.");
    }

    private static Type NullableOf(Type type) => type.IsValueType && Nullable.GetUnderlyingType(type) is null
        ? type == typeof(long) ? typeof(long?) : type == typeof(decimal) ? typeof(decimal?) : type == typeof(double) ? typeof(double?)
          : type == typeof(int) ? typeof(int?) : type == typeof(DateTime) ? typeof(DateTime?) : type == typeof(DateTimeOffset) ? typeof(DateTimeOffset?)
          : type == typeof(float) ? typeof(float?) : type == typeof(short) ? typeof(short?) : type == typeof(byte) ? typeof(byte?)
          : type == typeof(Guid) ? typeof(Guid?) : type == typeof(TimeSpan) ? typeof(TimeSpan?) : type == typeof(DateOnly) ? typeof(DateOnly?)
          : type == typeof(bool) ? typeof(bool?) : typeof(object)
        : type;

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

        var sent = _serializer.Serialize(entity);
        var operation = new BatchOperation(Name<TEntity>(), kind, sent, ConnectionId<TEntity>());
        Action<byte[]> applyResult = kind == BatchOperationKind.Delete
            ? _ => { }
            : data => ApplyResult(data, sent, entity);
        _transaction.Add(operation, applyResult);
        return true;
    }

    private async Task<EntityResult> CommitAsync(RemoteTransaction transaction, CancellationToken ct)
    {
        if (!ReferenceEquals(_transaction, transaction))
            return EntityResult.Failure(EntityErrorCode.InvalidRequest, "This transaction is no longer active.");
        _transaction = null;

        if (transaction.Operations.Count == 0)
            return EntityResult.Success();

        var operations = transaction.Operations.Select(o => o.Operation).ToArray();
        TransportResult response;
        try
        {
            response = await SendBatchAsync(operations, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return EntityResult.FromException(ex);
        }

        if (!response.Successful)
            return response.ToDbResult();

        var results = response.Data is { Length: > 0 }
            ? _serializer.Deserialize<byte[][]>(response.Data) ?? []
            : [];

        // Generated keys and new versions copied back into the original entities.
        for (var i = 0; i < Math.Min(results.Length, transaction.Operations.Count); i++)
            transaction.Operations[i].ApplyResult(results[i]);
        return EntityResult.Success();
    }

    /// <summary>
    /// How many times a commit is sent again, with the same operation id, when the call fails (a lost answer, a dropped
    /// connection): the server applies it once and answers the first result. Not with a transport that cannot carry the id.
    /// </summary>
    public int BatchRetries { get; init; } = 2;

    private async Task<TransportResult> SendBatchAsync(BatchOperation[] operations, CancellationToken ct)
    {
        var operationId = Guid.NewGuid();
        for (var attempt = 0; ; attempt++)
        {
            TransportResult response;
            try
            {
                response = await RemoteDiagnostics.ClientAsync("batch", "batch", () => _remote.ExecuteBatchOnceAsync(operationId, operations, ct));
            }
            catch (Exception) when (attempt < BatchRetries && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct);
                continue;
            }

            // A transport without operation ids: a plain batch, sent once (no retry could tell a lost answer from a refusal).
            return response is { Successful: false, ErrorCode: EntityErrorCode.NotSupported } && attempt == 0
                ? await RemoteDiagnostics.ClientAsync("batch", "batch", () => _remote.ExecuteBatchAsync(operations, ct))
                : response;
        }
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

        public Task<EntityResult> CommitAsync(CancellationToken ct = default)
        {
            if (_done)
                return Task.FromResult(EntityResult.Failure(EntityErrorCode.InvalidRequest, "The transaction is already finished."));
            _done = true;
            return store.CommitAsync(this, ct);
        }

        public Task<EntityResult> RollbackAsync(CancellationToken ct = default)
        {
            _done = true;
            store.Abandon(this); // nothing was sent: forgetting the writes is enough
            return Task.FromResult(EntityResult.Success());
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

    private static async Task<byte[]?> CallAsync(string operation, string type, Func<Task<TransportResult>> call)
        => (await RemoteDiagnostics.ClientAsync(operation, type, call)).GetDataOrThrow();

    /// <summary>Name under which the server registers the type (see <see cref="EntityName"/>).</summary>
    private static string Name<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>() => EntityName.Of<TEntity>();

    private string? ConnectionId<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>()
        => session is not null && session.TryGetSession(typeof(TEntity), out var current) ? current.session : null;

    private IReadOnlyList<TEntity> DeserializeMany<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(byte[]? data)
        => data is { Length: > 0 } ? _serializer.Deserialize<TEntity[]>(data) ?? [] : [];

    /// <summary>
    /// Copies what the server wrote (generated keys, versions, dates) from the returned entity into the caller's one.
    /// False when the server returned nothing.
    /// </summary>
    /// <param name="sent">The entity as sent: what the server received, to tell its changes from what the serializer drops.</param>
    private bool ApplyResult<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(byte[]? data, byte[] sent, TEntity target)
        where TEntity : class
    {
        if (data is not { Length: > 0 })
            return false;

        var returned = _serializer.Deserialize<TEntity>(data);
        if (returned is null)
            return false;

        if (_serializer.Deserialize<TEntity>(sent) is { } received)
            CopyChanges(typeof(TEntity), returned, received, target, depth: 0);
        return true;
    }

    /// <summary>
    /// Copies the scalar properties the server changed, comparing what it returned with what it received, and walks child
    /// lists item by item (the server keeps their order). The caller's objects are kept: navigation properties, members
    /// the serializer does not carry and child instances held elsewhere (a bound list...) are left as they are, and
    /// receive the generated keys.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification =
        "Child entities are serialized by the transport, which needs their public properties too.")]
    private static void CopyChanges([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type,
        object returned, object received, object target, int depth)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0)
                continue;

            var value = property.GetValue(returned);
            if (IsScalar(property.PropertyType))
            {
                if (!SameValue(value, property.GetValue(received)))
                    property.SetValue(target, value);
            }
            else if (depth < MaxDepth && value is not null and not IList && property.GetValue(received) is { } receivedOne
                && property.GetValue(target) is { } targetOne && value.GetType() == targetOne.GetType())
            {
                CopyChanges(targetOne.GetType(), value, receivedOne, targetOne, depth + 1);      // one child: its key and version
            }
            else if (depth < MaxDepth
                && value is IList returnedItems
                && property.GetValue(received) is IList receivedItems
                && property.GetValue(target) is IList targetItems
                && returnedItems.Count == targetItems.Count && receivedItems.Count == targetItems.Count)
            {
                for (var i = 0; i < targetItems.Count; i++)
                {
                    if (returnedItems[i] is { } returnedItem && receivedItems[i] is { } receivedItem && targetItems[i] is { } targetItem
                        && returnedItem.GetType() == targetItem.GetType())
                        CopyChanges(targetItem.GetType(), returnedItem, receivedItem, targetItem, depth + 1);
                }
            }
        }
    }

    private static bool SameValue(object? a, object? b)
        => a is byte[] x && b is byte[] y ? x.AsSpan().SequenceEqual(y) : Equals(a, b);

    private const int MaxDepth = 4;

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(byte[])
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid)
            || type == typeof(DateOnly) || type == typeof(TimeOnly);
    }
}

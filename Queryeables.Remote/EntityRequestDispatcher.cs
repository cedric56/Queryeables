using Queryeables.Abstraction;

namespace Queryeables.Remote;

/// <summary>
/// Server side: routes each call to the <see cref="EntityHandler"/> of the requested type. A transport (the MagicOnion
/// service, an HTTP endpoint...) only has to forward its calls here, with the caller (<see cref="EntityRequestContext"/>).
/// </summary>
/// <param name="registry">The exposed entity types.</param>
/// <param name="serializer">Reads and writes the entities; the clients must use the same.</param>
/// <param name="transactionStoreFactory">
/// Store used for batches (<see cref="ExecuteBatchAsync"/>): all their writes go through a single
/// transaction of this store. Without it, batches are rejected (NotSupported). The types of a batch must live
/// in this store's database.
/// </param>
public sealed class EntityRequestDispatcher(
    IEntityHandlerRegistry registry,
    IEntitySerializer serializer,
    Func<IEntityStore>? transactionStoreFactory = null)
{
    private readonly IEntityHandlerRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly IEntitySerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

    /// <inheritdoc cref="EntityRequestDispatcher"/>
    public EntityRequestDispatcher(IEntityHandlerRegistry registry, IEntitySerializer serializer, IEntityStoreFactory? transactionStores)
        : this(registry, serializer, transactionStores is null ? null : transactionStores.Create)
    {
    }

    public Task<TransportResult> GetAllAsync(string type, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, "get_all", h => h.GetAllAsync(_serializer, Reading(context), ct));

    public Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, bool any,
        EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, "select", h => h.SelectAsync(_serializer, conditions ?? [], any, Reading(context), ct));

    public Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, "select_paged", h => h.SelectPagedAsync(_serializer, conditions ?? [], any, pageNumber, pageSize, Reading(context), ct));

    public Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        SortKey[] sort, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, "select_paged", h => h.SelectPagedAsync(_serializer, conditions ?? [], any, pageNumber, pageSize, sort ?? [], Reading(context), ct));

    public Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, "get_changes", h => h.GetChangesAsync(_serializer, sinceUtc, Reading(context), ct));

    /// <summary>A count, an aggregate, a projection, a patch, a write of many rows, a graph save (<see cref="RemoteRequestKind"/>).</summary>
    public Task<TransportResult> ExecuteAsync(string type, RemoteRequest request, EntityRequestContext context, CancellationToken ct = default)
        => request is null
            ? Task.FromResult(TransportResult.Failure(EntityErrorCode.InvalidRequest, "Empty request."))
            : Dispatch(type, "execute_" + request.Kind.ToString().ToLowerInvariant(),
                h => h.ExecuteAsync(_serializer, request, Verified(context.WithConnection(request.ConnectionId)), ct));

    /// <summary>A read: the handlers go with it, for the filters that go through a relation toward another exposed type.</summary>
    private EntityRequestContext Reading(EntityRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context with { Handlers = _registry };
    }

    public Task<TransportResult> InsertAsync(string type, byte[] entity, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, "insert", h => h.WriteAsync(_serializer, BatchOperationKind.Insert, entity, Verified(context), ct));

    public Task<TransportResult> UpdateAsync(string type, byte[] entity, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, "update", h => h.WriteAsync(_serializer, BatchOperationKind.Update, entity, Verified(context), ct));

    public Task<TransportResult> DeleteAsync(string type, byte[] entity, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, "delete", h => h.WriteAsync(_serializer, BatchOperationKind.Delete, entity, Verified(context), ct));

    /// <summary>
    /// The results of the writes already applied (<see cref="WriteOnceAsync"/>): in memory by default; a durable store
    /// also covers a restart of the server.
    /// </summary>
    public IIdempotencyStore Idempotency { get; init; } = new MemoryIdempotencyStore();

    /// <summary>
    /// The hub connections and their users: a write names its author's connection (so that the author does not receive its
    /// own change), and that name is kept only when the connection belongs to the caller. Null: names are not checked
    /// (a server in process, without hubs).
    /// </summary>
    public ConnectionOwners? Connections { get; init; }

    /// <summary>
    /// Send the message of an unexpected error (a database error...) to the client. Off by default: the client gets a
    /// generic message with a reference, and <see cref="UnexpectedError"/> receives the exception under that reference;
    /// database messages name tables, constraints and values. Turn it on in development only.
    /// </summary>
    public bool DetailedErrors { get; init; }

    /// <summary>Receives each unexpected error with the reference given to the client (to log it).</summary>
    public Action<Exception, string>? UnexpectedError { get; init; }

    /// <summary>Most operations in one batch (one transaction): a client cannot hold the database with a huge one.</summary>
    public int MaxBatchSize { get; init; } = 100;

    private EntityRequestContext Verified(EntityRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context = context with { Handlers = _registry };
        return Connections is null || context.ConnectionId is null
            ? context
            : context.WithConnection(Connections.Verify(context.ConnectionId, context.User));
    }

    /// <summary>
    /// A write applied once per <paramref name="operationId"/> and user: sent again (its answer was lost), it gets the first
    /// result back. A refused write is not remembered: sent again, it is checked again.
    /// </summary>
    public Task<TransportResult> WriteOnceAsync(Guid operationId, string type, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (operationId == Guid.Empty)
            return Task.FromResult(TransportResult.Failure(EntityErrorCode.InvalidRequest, "An operation id is required."));
        if (!Enum.IsDefined(kind))
            return Task.FromResult(TransportResult.Failure(EntityErrorCode.InvalidRequest, $"Unknown write: {kind}."));

        if (string.IsNullOrWhiteSpace(type))
            return Task.FromResult(UnknownType(type));
        if (IdempotencyKey.Of(operationId, context.User, type, kind) is not { } key)
            return Task.FromResult(TransportResult.Failure(EntityErrorCode.InvalidRequest,
                "The user has no stable identity (neither a name identifier nor a name): its writes cannot be told from another's."));

        return Idempotency.RunOnceAsync(key, () => kind switch
        {
            BatchOperationKind.Insert => InsertAsync(type, entity, context, ct),
            BatchOperationKind.Update => UpdateAsync(type, entity, context, ct),
            _ => DeleteAsync(type, entity, context, ct),
        }, ct);
    }

    /// <summary>
    /// <see cref="ExecuteBatchAsync"/> applied once per <paramref name="operationId"/> and user: sent again (its answer was
    /// lost), it gets the first result back instead of writing again. A refused batch is not remembered.
    /// </summary>
    public Task<TransportResult> ExecuteBatchOnceAsync(Guid operationId, BatchOperation[] operations, EntityRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (operationId == Guid.Empty)
            return Task.FromResult(TransportResult.Failure(EntityErrorCode.InvalidRequest, "An operation id is required."));
        // A user without a stable identity cannot own an operation id: NotSupported makes the client send a plain batch,
        // once, as before.
        if (IdempotencyKey.OfBatch(operationId, context.User) is not { } key)
            return Task.FromResult(TransportResult.Failure(EntityErrorCode.NotSupported,
                "The user has no stable identity (neither a name identifier nor a name): a batch cannot be applied once per id."));
        return Idempotency.RunOnceAsync(key, () => ExecuteBatchAsync(operations, context, ct), ct);
    }

    /// <summary>
    /// Runs every operation in a single transaction: if one fails (permissions, validation, conflict,
    /// vanished row), none is applied. Returns one serialized result per operation.
    /// </summary>
    public Task<TransportResult> ExecuteBatchAsync(BatchOperation[] operations, EntityRequestContext context, CancellationToken ct = default)
    {
        if (transactionStoreFactory is null)
            return Task.FromResult(TransportResult.Failure(EntityErrorCode.NotSupported,
                "This server does not accept batches: no transactional store is configured."));

        if (operations is null || operations.Length == 0)
            return Task.FromResult(TransportResult.Success(_serializer.Serialize(Array.Empty<byte[]>())));
        if (operations.Length > MaxBatchSize)
            return Task.FromResult(TransportResult.Failure(EntityErrorCode.InvalidRequest,
                $"Too many operations in one batch: {operations.Length}, at most {MaxBatchSize}."));

        // Every type is checked before opening the transaction.
        var handlers = new EntityHandler[operations.Length];
        for (var i = 0; i < operations.Length; i++)
        {
            if (!_registry.TryGet(operations[i].Type, out handlers[i]))
                return Task.FromResult(UnknownType(operations[i].Type));
            if (SerializerProblem(handlers[i]) is { } problem)
                return Task.FromResult(problem);
        }

        return RemoteDiagnostics.ServerAsync("batch", "batch", async () => Hide(await TransportResult.RunAsync(async () =>
        {
            var results = new byte[operations.Length][];
            await using var store = transactionStoreFactory();
            var outcome = await store.RunInTransactionAsync(async s =>
            {
                for (var i = 0; i < operations.Length; i++)
                {
                    var operation = operations[i];
                    results[i] = await handlers[i].WriteAsync(_serializer, s, operation.Kind, operation.Entity,
                        Verified(context.WithConnection(operation.ConnectionId)), requireEffect: true, ct);
                }
            }, ct);
            // A failed batch is reported with its own error kind (conflict, validation...).
            outcome.ThrowIfFailed();
            return _serializer.Serialize(results);
        })));
    }

    /// <summary>
    /// This dispatcher as an <see cref="IEntityTransport"/>, called in-process with the given caller: a
    /// <see cref="RemoteEntityStore"/> on top of it behaves as a remote client, without any network. Useful for tests,
    /// and for an application that hosts its own server.
    /// </summary>
    public IEntityTransport AsTransport(EntityRequestContext? context = null) => new InProcessRemote(this, context ?? EntityRequestContext.Anonymous);

    private Task<TransportResult> Dispatch(string type, string operation, Func<EntityHandler, Task<TransportResult>> call)
        => !string.IsNullOrWhiteSpace(type) && _registry.TryGet(type, out var handler)
            ? SerializerProblem(handler) is { } problem
                ? Task.FromResult(problem)
                : RemoteDiagnostics.ServerAsync(operation, type, async () => Hide(await call(handler)))
            : Task.FromResult(UnknownType(type));

    // Checked once per type: a property hidden by an attribute this serializer ignores would reach every client.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<EntityHandler, string?> _serializerProblems = new();

    private TransportResult? SerializerProblem(EntityHandler handler)
    {
        var problem = _serializerProblems.GetOrAdd(handler, h => h.CheckSerializer(_serializer));
        if (problem is null)
            return null;
        UnexpectedError?.Invoke(new InvalidOperationException(problem), "configuration");
        return TransportResult.Failure(EntityErrorCode.NotSupported, $"{handler.Type.Name} cannot be served: the server is misconfigured.");
    }

    /// <summary>An unexpected error leaves the server as a reference; the exception goes to <see cref="UnexpectedError"/>.</summary>
    private TransportResult Hide(TransportResult result)
    {
        if (result.ErrorCode != EntityErrorCode.Unknown || result.Successful || DetailedErrors)
            return result;

        var reference = Guid.NewGuid().ToString("N")[..12];
        System.Diagnostics.Activity.Current?.SetTag("queryeables.error.reference", reference);
        if (result.Cause is { } cause)
            UnexpectedError?.Invoke(cause, reference);
        return TransportResult.Failure(EntityErrorCode.Unknown, $"An unexpected error occurred on the server (reference {reference}).");
    }

    private static TransportResult UnknownType(string? type)
        => TransportResult.Failure(EntityErrorCode.UnknownType, $"Type not exposed by the server: {type}.");

    private sealed class InProcessRemote(EntityRequestDispatcher dispatcher, EntityRequestContext caller) : IEntityTransport
    {
        public Task<TransportResult> GetAllAsync(string type, CancellationToken ct = default)
            => dispatcher.GetAllAsync(type, caller, ct);

        public Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, bool any, CancellationToken ct = default)
            => dispatcher.SelectAsync(type, conditions, any, caller, ct);

        public Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
            CancellationToken ct = default)
            => dispatcher.SelectPagedAsync(type, conditions, any, pageNumber, pageSize, caller, ct);

        public Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
            SortKey[] sort, CancellationToken ct = default)
            => dispatcher.SelectPagedAsync(type, conditions, any, pageNumber, pageSize, sort, caller, ct);

        public Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default)
            => dispatcher.GetChangesAsync(type, sinceUtc, caller, ct);

        public Task<TransportResult> InsertAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
            => dispatcher.InsertAsync(type, entity, caller.WithConnection(connectionId), ct);

        public Task<TransportResult> UpdateAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
            => dispatcher.UpdateAsync(type, entity, caller.WithConnection(connectionId), ct);

        public Task<TransportResult> DeleteAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
            => dispatcher.DeleteAsync(type, entity, caller.WithConnection(connectionId), ct);

        public Task<TransportResult> ExecuteBatchAsync(BatchOperation[] operations, CancellationToken ct = default)
            => dispatcher.ExecuteBatchAsync(operations, caller, ct);

        public Task<TransportResult> ExecuteAsync(string type, RemoteRequest request, CancellationToken ct = default)
            => dispatcher.ExecuteAsync(type, request, caller, ct);

        public Task<TransportResult> ExecuteBatchOnceAsync(Guid operationId, BatchOperation[] operations, CancellationToken ct = default)
            => dispatcher.ExecuteBatchOnceAsync(operationId, operations, caller, ct);

        public Task<TransportResult> WriteOnceAsync(Guid operationId, BatchOperationKind kind, string type, byte[] entity, string? connectionId,
            CancellationToken ct = default)
            => dispatcher.WriteOnceAsync(operationId, type, kind, entity, caller.WithConnection(connectionId), ct);
    }
}

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
        => Dispatch(type, h => h.GetAllAsync(_serializer, context, ct));

    public Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, bool any,
        EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, h => h.SelectAsync(_serializer, conditions ?? [], any, context, ct));

    public Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, h => h.SelectPagedAsync(_serializer, conditions ?? [], any, pageNumber, pageSize, context, ct));

    public Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        SortKey[] sort, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, h => h.SelectPagedAsync(_serializer, conditions ?? [], any, pageNumber, pageSize, sort ?? [], context, ct));

    public Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, h => h.GetChangesAsync(_serializer, sinceUtc, context, ct));

    public Task<TransportResult> InsertAsync(string type, byte[] entity, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, h => h.WriteAsync(_serializer, BatchOperationKind.Insert, entity, context, ct));

    public Task<TransportResult> UpdateAsync(string type, byte[] entity, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, h => h.WriteAsync(_serializer, BatchOperationKind.Update, entity, context, ct));

    public Task<TransportResult> DeleteAsync(string type, byte[] entity, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, h => h.WriteAsync(_serializer, BatchOperationKind.Delete, entity, context, ct));

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

        // Every type is checked before opening the transaction.
        var handlers = new EntityHandler[operations.Length];
        for (var i = 0; i < operations.Length; i++)
        {
            if (!_registry.TryGet(operations[i].Type, out handlers[i]))
                return Task.FromResult(UnknownType(operations[i].Type));
        }

        return TransportResult.RunAsync(async () =>
        {
            var results = new byte[operations.Length][];
            await using var store = transactionStoreFactory();
            var outcome = await store.RunInTransactionAsync(async s =>
            {
                for (var i = 0; i < operations.Length; i++)
                {
                    var operation = operations[i];
                    results[i] = await handlers[i].WriteAsync(_serializer, s, operation.Kind, operation.Entity,
                        context.WithConnection(operation.ConnectionId), requireEffect: true, ct);
                }
            }, ct);
            // A failed batch is reported with its own error kind (conflict, validation...).
            outcome.ThrowIfFailed();
            return _serializer.Serialize(results);
        });
    }

    /// <summary>
    /// This dispatcher as an <see cref="IEntityTransport"/>, called in-process with the given caller: a
    /// <see cref="RemoteEntityStore"/> on top of it behaves as a remote client, without any network. Useful for tests,
    /// and for an application that hosts its own server.
    /// </summary>
    public IEntityTransport AsTransport(EntityRequestContext? context = null) => new InProcessRemote(this, context ?? EntityRequestContext.Anonymous);

    private Task<TransportResult> Dispatch(string type, Func<EntityHandler, Task<TransportResult>> call)
        => !string.IsNullOrWhiteSpace(type) && _registry.TryGet(type, out var handler)
            ? call(handler)
            : Task.FromResult(UnknownType(type));

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
    }
}

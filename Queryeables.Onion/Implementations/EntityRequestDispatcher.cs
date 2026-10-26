using MessagePack;
using Queryeables.Abstraction;

namespace Queryeables.Onion;

/// <summary>
/// Routes each <see cref="IMagicOnionLayer"/> call to the <see cref="EntityHandler"/> of the requested type.
/// The server-side MagicOnion service (<c>MagicOnionService</c>) only has to delegate.
/// </summary>
/// <param name="transactionStoreFactory">
/// Store used for batches (<see cref="ExecuteBatchAsync"/>): all their writes go through a single
/// transaction of this store. Without it, batches are rejected (NotSupported). The types of a batch must live
/// in this store's database.
/// </param>
public sealed class EntityRequestDispatcher(
    IEntityHandlerRegistry registry,
    Func<IEntityStore>? transactionStoreFactory = null)
{
    public Task<DBResultMessage> GetAllAsync(string type, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, h => h.GetAllAsync(context, ct));

    public Task<DBResultMessage> SelectAsync(string type, FilterCondition[] conditions, bool any,
        EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, h => h.SelectAsync(conditions ?? [], any, context, ct));

    public Task<DBResultMessage> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, h => h.SelectPagedAsync(conditions ?? [], any, pageNumber, pageSize, context, ct));

    public Task<DBResultMessage> GetChangesAsync(string type, DateTime sinceUtc, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, h => h.GetChangesAsync(sinceUtc, context, ct));

    public Task<DBResultMessage> InsertAsync(string type, byte[] entity, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, h => h.WriteAsync(BatchOperationKind.Insert, entity, context, ct));

    public Task<DBResultMessage> UpdateAsync(string type, byte[] entity, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, h => h.WriteAsync(BatchOperationKind.Update, entity, context, ct));

    public Task<DBResultMessage> DeleteAsync(string type, byte[] entity, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, h => h.WriteAsync(BatchOperationKind.Delete, entity, context, ct));

    /// <summary>
    /// Runs every operation in a single transaction: if one fails (permissions, validation, conflict,
    /// vanished row), none is applied. Returns one serialized result per operation.
    /// </summary>
    public Task<DBResultMessage> ExecuteBatchAsync(BatchOperation[] operations, EntityRequestContext context, CancellationToken ct = default)
    {
        if (transactionStoreFactory is null)
            return Task.FromResult(DBResultMessage.Failure(EntityErrorCode.NotSupported,
                "This server does not accept batches: no transactional store is configured."));

        if (operations is null || operations.Length == 0)
            return Task.FromResult(DBResultMessage.Success(MessagePackSerializer.Serialize(Array.Empty<byte[]>(), cancellationToken: ct)));

        // Every type is checked before opening the transaction.
        var handlers = new EntityHandler[operations.Length];
        for (var i = 0; i < operations.Length; i++)
        {
            if (!registry.TryGet(operations[i].Type, out handlers[i]))
                return Task.FromResult(UnknownType(operations[i].Type));
        }

        return DBResultMessage.RunAsync(async () =>
        {
            var results = new byte[operations.Length][];
            await using var store = transactionStoreFactory();
            var outcome = await store.RunInTransactionAsync(async s =>
            {
                for (var i = 0; i < operations.Length; i++)
                {
                    var operation = operations[i];
                    results[i] = await handlers[i].WriteAsync(s, operation.Kind, operation.Entity,
                        context.WithConnection(operation.ConnectionId), requireEffect: true, ct);
                }
            }, ct);
            // A failed batch is reported with its own error kind (conflict, validation...).
            outcome.ThrowIfFailed();
            return MessagePackSerializer.Serialize(results, cancellationToken: ct);
        });
    }

    private Task<DBResultMessage> Dispatch(string type, Func<EntityHandler, Task<DBResultMessage>> call)
        => !string.IsNullOrWhiteSpace(type) && registry.TryGet(type, out var handler)
            ? call(handler)
            : Task.FromResult(UnknownType(type));

    private static DBResultMessage UnknownType(string? type)
        => DBResultMessage.Failure(EntityErrorCode.UnknownType, $"Type not exposed by the server: {type}.");
}

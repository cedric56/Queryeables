using Queryeables.Abstraction;
using Queryeables.Remote;

namespace Queryeables.Onion;

/// <summary>
/// The MagicOnion transport of the client: <see cref="IEntityTransport"/> over the <see cref="IMagicOnionLayer"/> service.
/// </summary>
/// <example>
/// <code>
/// var layer = MagicOnionClient.Create&lt;IMagicOnionLayer&gt;(channel);
/// IEntityStoreFactory stores = MagicOnionTransport.Stores(layer, session);
/// await using var store = stores.Create();
/// </code>
/// </example>
public sealed class MagicOnionTransport(IMagicOnionLayer layer) : IEntityTransport
{
    private readonly IMagicOnionLayer _layer = layer ?? throw new ArgumentNullException(nameof(layer));

    /// <summary>Stores that reach the server through <paramref name="layer"/>, with MessagePack.</summary>
    public static IEntityStoreFactory Stores(IMagicOnionLayer layer, IChangeSession? session = null, IEntitySerializer? serializer = null)
        => RemoteEntityStore.Factory(new MagicOnionTransport(layer), serializer ?? MessagePackEntitySerializer.Default, session);

    public async Task<TransportResult> GetAllAsync(string type, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).GetAllAsync(type)).ToTransportResult();

    public async Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, bool any, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).SelectAsync(type, FilterConditionMessage.From(conditions), any)).ToTransportResult();

    public async Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct)
            .SelectPagedAsync(type, FilterConditionMessage.From(conditions), any, pageNumber, pageSize)).ToTransportResult();

    public async Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        SortKey[] sort, CancellationToken ct = default)
        => sort is not { Length: > 0 }
            ? await SelectPagedAsync(type, conditions, any, pageNumber, pageSize, ct)
            : (await _layer.WithCancellationToken(ct).SelectSortedPageAsync(type, FilterConditionMessage.From(conditions), any,
                pageNumber, pageSize, SortKeyMessage.From(sort))).ToTransportResult();

    public async Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).GetChangesAsync(type, sinceUtc)).ToTransportResult();

    public async Task<TransportResult> InsertAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).InsertAsync(type, entity, connectionId)).ToTransportResult();

    public async Task<TransportResult> UpdateAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).UpdateAsync(type, entity, connectionId)).ToTransportResult();

    public async Task<TransportResult> DeleteAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).DeleteAsync(type, entity, connectionId)).ToTransportResult();

    public async Task<TransportResult> ExecuteBatchAsync(BatchOperation[] operations, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).ExecuteBatchAsync(BatchOperationMessage.From(operations))).ToTransportResult();
}

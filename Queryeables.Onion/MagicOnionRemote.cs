using Queryeables.Abstraction;
using Queryeables.Remote;

namespace Queryeables.Onion;

/// <summary>
/// The MagicOnion transport of the client: <see cref="IEntityRemote"/> over the <see cref="IMagicOnionLayer"/> service.
/// </summary>
/// <example>
/// <code>
/// var layer = MagicOnionClient.Create&lt;IMagicOnionLayer&gt;(channel);
/// IEntityStoreFactory stores = MagicOnionRemote.Stores(layer, session);
/// await using var store = stores.Create();
/// </code>
/// </example>
public sealed class MagicOnionRemote(IMagicOnionLayer layer) : IEntityRemote
{
    private readonly IMagicOnionLayer _layer = layer ?? throw new ArgumentNullException(nameof(layer));

    /// <summary>Stores that reach the server through <paramref name="layer"/>, with MessagePack.</summary>
    public static IEntityStoreFactory Stores(IMagicOnionLayer layer, IDbSession? session = null, IEntitySerializer? serializer = null)
        => RemoteEntityStore.Factory(new MagicOnionRemote(layer), serializer ?? MessagePackEntitySerializer.Default, session);

    public async Task<RemoteResult> GetAllAsync(string type, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).GetAllAsync(type)).ToRemoteResult();

    public async Task<RemoteResult> SelectAsync(string type, FilterCondition[] conditions, bool any, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).SelectAsync(type, FilterConditionMessage.From(conditions), any)).ToRemoteResult();

    public async Task<RemoteResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct)
            .SelectPagedAsync(type, FilterConditionMessage.From(conditions), any, pageNumber, pageSize)).ToRemoteResult();

    public async Task<RemoteResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).GetChangesAsync(type, sinceUtc)).ToRemoteResult();

    public async Task<RemoteResult> InsertAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).InsertAsync(type, entity, connectionId)).ToRemoteResult();

    public async Task<RemoteResult> UpdateAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).UpdateAsync(type, entity, connectionId)).ToRemoteResult();

    public async Task<RemoteResult> DeleteAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).DeleteAsync(type, entity, connectionId)).ToRemoteResult();

    public async Task<RemoteResult> ExecuteBatchAsync(BatchOperation[] operations, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).ExecuteBatchAsync(BatchOperationMessage.From(operations))).ToRemoteResult();
}

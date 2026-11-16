using Queryeables.Abstraction;
using Queryeables.Remote;

namespace Queryeables.MagicOnion;

/// <summary>
/// The MagicOnion transport of the client: <see cref="IEntityTransport"/> over the <see cref="IMagicOnionLayer"/> service.
/// </summary>
/// <example>
/// <code>
/// var layer = MagicOnionClient.Create&lt;IMagicOnionLayer&gt;(channel);
/// IEntityStoreFactory stores = MagicOnionTransport.Stores(layer, JsonEntitySerializer.Default);
/// await using var store = stores.Create();
/// </code>
/// </example>
public sealed class MagicOnionTransport(IMagicOnionLayer layer) : IEntityTransport
{
    private readonly IMagicOnionLayer _layer = layer ?? throw new ArgumentNullException(nameof(layer));

    /// <summary>
    /// Stores that read the replicas the server publishes through <paramref name="layer"/>, with <paramref name="serializer"/>
    /// (the server's).
    /// </summary>
    public static IEntityStoreFactory Stores(IMagicOnionLayer layer, IEntitySerializer serializer)
        => RemoteEntityStore.Factory(new MagicOnionTransport(layer), serializer ?? throw new ArgumentNullException(nameof(serializer)));

    public async Task<TransportResult> GetAllAsync(string type, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).GetAllAsync(type)).ToTransportResult();

    public async Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).SelectAsync(type, FilterConditionMessage.From(conditions))).ToTransportResult();

    public async Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default)
        => (await _layer.WithCancellationToken(ct).GetChangesAsync(type, sinceUtc)).ToTransportResult();
}

using MagicOnion.Server.Hubs;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using Queryeables.Onion;

namespace Queryeables.Server;

/// <summary>
/// Broadcasts the changes of one entity type to MagicOnion clients (see <see cref="ChangeBroadcaster{TObject}"/>).
/// Register it as a singleton.
/// </summary>
public sealed class ChangeHubConnections<TObject>(
    IChangeFeed<TObject> monitor,
    ChangeFeedRegistry? registry = null,
    ILogger<ChangeHubConnections<TObject>>? logger = null)
    : ChangeBroadcaster<TObject>(monitor, registry) where TObject : class
{
    // Shared broadcast group, captured on the first subscription.
    private volatile IGroup<IChangeHubReceiver<TObject>>? _group;

    public void RegisterGroup(IGroup<IChangeHubReceiver<TObject>> group, Guid contextId)
    {
        _group ??= group;
        if (AddSubscriber(contextId.ToString()))
            logger?.LogInformation("{ContextId} subscribed", contextId);
    }

    public void Unregister(Guid contextId)
    {
        if (RemoveSubscriber(contextId.ToString()))
            logger?.LogInformation("{ContextId} unsubscribed", contextId);
    }

    /// <summary>Broadcasts a change reported by a client.</summary>
    public void Raise(EntityChange<TObject> record) => Relay(record);

    protected override Task SendAsync(EntityChange<TObject> record, IReadOnlyList<string> excludedConnections, CancellationToken ct)
    {
        // Ids that are not MagicOnion connections (SignalR session...) are ignored.
        var excluded = excludedConnections
            .Select(id => Guid.TryParse(id, out var guid) ? guid : (Guid?)null)
            .OfType<Guid>()
            .ToList();

        logger?.LogDebug("Broadcasting {Record}", record);
        _group?.Except(excluded).OnChanged(ChangeMessage.From(record));
        return Task.CompletedTask;
    }

    protected override void OnError(Exception exception)
        => logger?.LogError(exception, "Failed to broadcast a change.");
}

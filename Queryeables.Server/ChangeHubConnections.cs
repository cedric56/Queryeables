using MagicOnion.Server.Hubs;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
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
    ILogger<ChangeHubConnections<TObject>>? logger = null,
    ChangeHubOptions? options = null,
    ConnectionOwners? owners = null)
    : ChangeBroadcaster<TObject>(monitor, registry, options, owners) where TObject : class
{
    // Shared broadcast group, captured on the first subscription.
    private volatile IGroup<IChangeHubReceiver<TObject>>? _group;

    public void RegisterGroup(IGroup<IChangeHubReceiver<TObject>> group, Guid contextId, ClaimsPrincipal? user = null)
    {
        _group ??= group;
        if (AddSubscriber(contextId.ToString(), user))
            logger?.LogInformation("{ContextId} subscribed", contextId);
    }

    public void Unregister(Guid contextId)
    {
        if (RemoveSubscriber(contextId.ToString()))
            logger?.LogInformation("{ContextId} unsubscribed", contextId);
    }

    /// <summary>A change reported by the client of <paramref name="contextId"/>: see <see cref="ChangeBroadcaster{TObject}.RelayFromClient"/>.</summary>
    public bool Raise(EntityChange<TObject> record, Guid contextId)
    {
        var relayed = RelayFromClient(record, contextId.ToString());
        if (!relayed)
            logger?.LogDebug("Change from {ContextId} not relayed: {Record}", contextId, record);
        return relayed;
    }

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

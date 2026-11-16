using MagicOnion.Server.Hubs;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using Queryeables.Abstraction;
using Queryeables.MagicOnion;

namespace Queryeables.MagicOnion.Server;

/// <summary>
/// Broadcasts the changes of one entity type to MagicOnion clients (see <see cref="ChangeBroadcaster{TObject}"/>).
/// Register it as a singleton.
/// </summary>
public sealed class ChangeHubConnections<TObject>(
    IChangeFeed<TObject> monitor,
    ChangeFeedRegistry? registry = null,
    ILogger<ChangeHubConnections<TObject>>? logger = null,
    ChangeHubOptions? options = null)
    : ChangeBroadcaster<TObject>(monitor, registry, options) where TObject : class
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

    protected override Task SendAsync(EntityChange<TObject> record, CancellationToken ct)
    {
        logger?.LogDebug("Broadcasting {Record}", record);
        _group?.All.OnChanged(ChangeMessage.From(record));
        return Task.CompletedTask;
    }

    protected override Task SendToAsync(EntityChange<TObject> record, IReadOnlyList<string> recipients, CancellationToken ct)
    {
        var only = recipients.Select(id => Guid.TryParse(id, out var guid) ? guid : (Guid?)null).OfType<Guid>().ToList();
        if (only.Count > 0)
            _group?.Only(only).OnChanged(ChangeMessage.From(record));
        return Task.CompletedTask;
    }

    protected override void OnError(Exception exception)
        => logger?.LogError(exception, "Failed to broadcast a change.");
}

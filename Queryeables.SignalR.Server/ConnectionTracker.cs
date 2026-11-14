using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using Queryeables.Abstraction;

namespace Queryeables.SignalR.Server;

/// <summary>
/// Broadcasts the changes of one entity type to SignalR clients (see <see cref="ChangeBroadcaster{TObject}"/>).
/// Register it as a singleton.
/// </summary>
public sealed class ConnectionTracker<TObject>(
    IHubContext<SignalRHub<TObject>> hubContext,
    IChangeFeed<TObject> monitor,
    ChangeFeedRegistry? registry = null,
    ILogger<ConnectionTracker<TObject>>? logger = null,
    ChangeHubOptions? options = null,
    ConnectionOwners? owners = null)
    : ChangeBroadcaster<TObject>(monitor, registry, options, owners) where TObject : class
{
    public void Add(string connectionId, ClaimsPrincipal? user = null)
    {
        if (AddSubscriber(connectionId, user))
            logger?.LogInformation("Client connected: {ConnectionId}", connectionId);
    }

    public void Remove(string connectionId)
    {
        if (RemoveSubscriber(connectionId))
            logger?.LogInformation("Client disconnected: {ConnectionId}", connectionId);
    }

    protected override Task SendAsync(EntityChange<TObject> record, IReadOnlyList<string> excludedConnections, CancellationToken ct)
    {
        logger?.LogDebug("Broadcasting {Record}", record);
        return hubContext.Clients.AllExcept(excludedConnections).SendAsync(SignalRContract.DbChanged, SignalRChange.From(record), ct);
    }

    // Clients(...) names the connections: none of another server (backplane), none arrived after the check.
    protected override Task SendToAsync(EntityChange<TObject> record, IReadOnlyList<string> recipients, CancellationToken ct)
        => recipients.Count == 0
            ? Task.CompletedTask
            : hubContext.Clients.Clients(recipients).SendAsync(SignalRContract.DbChanged, SignalRChange.From(record), ct);

    protected override void OnError(Exception exception)
        => logger?.LogError(exception, "Failed to notify the clients of a change.");
}

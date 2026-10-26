using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;

namespace Queryeables.SignalR.Server;

/// <summary>
/// Broadcasts the changes of one entity type to SignalR clients (see <see cref="ChangeBroadcaster{TObject}"/>).
/// Register it as a singleton.
/// </summary>
public sealed class ConnectionTracker<TObject>(
    IHubContext<SignalRHub<TObject>> hubContext,
    IDbMonitor<TObject> monitor,
    DbMonitorRegistry? registry = null,
    ILogger<ConnectionTracker<TObject>>? logger = null)
    : ChangeBroadcaster<TObject>(monitor, registry) where TObject : class
{
    public void Add(string connectionId)
    {
        if (AddSubscriber(connectionId))
            logger?.LogInformation("Client connecté : {ConnectionId}", connectionId);
    }

    public void Remove(string connectionId)
    {
        if (RemoveSubscriber(connectionId))
            logger?.LogInformation("Client déconnecté : {ConnectionId}", connectionId);
    }

    protected override Task SendAsync(Record<TObject> record, IReadOnlyList<string> excludedConnections, CancellationToken ct)
    {
        logger?.LogDebug("Diffusion de {Record}", record);
        return hubContext.Clients.AllExcept(excludedConnections).SendAsync(SignalRContract.DbChanged, SignalRRecord.From(record), ct);
    }

    protected override void OnError(Exception exception)
        => logger?.LogError(exception, "Échec de la notification d'un changement aux clients.");
}

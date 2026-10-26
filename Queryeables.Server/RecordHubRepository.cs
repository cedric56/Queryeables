using MagicOnion.Server.Hubs;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using Queryeables.Onion;

namespace Queryeables.Server;

/// <summary>
/// Diffuse aux clients MagicOnion les changements d'un type d'entité (voir <see cref="ChangeBroadcaster{TObject}"/>).
/// À enregistrer en singleton.
/// </summary>
public sealed class RecordHubRepository<TObject>(
    IDbMonitor<TObject> monitor,
    DbMonitorRegistry? registry = null,
    ILogger<RecordHubRepository<TObject>>? logger = null)
    : ChangeBroadcaster<TObject>(monitor, registry) where TObject : class
{
    // Groupe de diffusion partagé, capturé à la première inscription.
    private volatile IGroup<IRecordHubReceiver<TObject>>? _group;

    public void RegisterGroup(IGroup<IRecordHubReceiver<TObject>> group, Guid contextId)
    {
        _group ??= group;
        if (AddSubscriber(contextId.ToString()))
            logger?.LogInformation("Abonnement de {ContextId}", contextId);
    }

    public void Unregister(Guid contextId)
    {
        if (RemoveSubscriber(contextId.ToString()))
            logger?.LogInformation("Désabonnement de {ContextId}", contextId);
    }

    /// <summary>Diffuse un changement signalé par un client.</summary>
    public void Raise(Record<TObject> record) => Relay(record);

    protected override Task SendAsync(Record<TObject> record, IReadOnlyList<string> excludedConnections, CancellationToken ct)
    {
        // Les identifiants qui ne sont pas des connexions MagicOnion (session SignalR...) sont ignorés.
        var excluded = excludedConnections
            .Select(id => Guid.TryParse(id, out var guid) ? guid : (Guid?)null)
            .OfType<Guid>()
            .ToList();

        logger?.LogDebug("Diffusion de {Record}", record);
        _group?.Except(excluded).OnChanged(record);
        return Task.CompletedTask;
    }

    protected override void OnError(Exception exception)
        => logger?.LogError(exception, "Échec de la diffusion d'un changement.");
}

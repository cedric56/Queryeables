using MagicOnion.Server.Hubs;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using Queryeables.Onion;

namespace Queryeables.Server;

/// <summary>
/// Suit les connexions abonnées aux changements d'un type d'entité et leur transmet les changements :
/// ceux de la base du serveur (via le moniteur) et ceux que les clients signalent. À enregistrer en singleton.
/// </summary>
public sealed class RecordHubRepository<TObject> : IDisposable where TObject : class
{
    private readonly HashSet<Guid> _subscribers = new();
    private readonly object _gate = new();

    private readonly IDbMonitor<TObject> _monitor;
    private readonly DbMonitorRegistry? _registry;
    private readonly ILogger? _logger;

    // Groupe de diffusion partagé, capturé à la première inscription.
    private IGroup<IRecordHubReceiver<TObject>>? _group;

    /// <param name="registry">Si fourni, le moniteur y est ajouté pour recevoir les changements de la base du serveur.</param>
    public RecordHubRepository(
        IDbMonitor<TObject> monitor,
        DbMonitorRegistry? registry = null,
        ILogger<RecordHubRepository<TObject>>? logger = null)
    {
        _monitor = monitor;
        _registry = registry;
        _logger = logger;

        _registry?.Add(monitor);
        _monitor.RecordChanged += OnRecordChanged;
    }

    public void RegisterGroup(IGroup<IRecordHubReceiver<TObject>> group, Guid contextId)
    {
        lock (_gate)
        {
            _group ??= group;
            _subscribers.Add(contextId);
        }
        _logger?.LogInformation("Abonnement de {ContextId}", contextId);
    }

    public void Unregister(Guid contextId)
    {
        bool removed;
        lock (_gate)
            removed = _subscribers.Remove(contextId);

        if (removed)
            _logger?.LogInformation("Désabonnement de {ContextId}", contextId);
    }

    /// <summary>Diffuse un changement à tous les abonnés, sauf ceux listés dans <see cref="Record{T}.Connections"/>.</summary>
    public void Raise(Record<TObject> record)
    {
        // Les identifiants qui ne sont pas des connexions MagicOnion (session SignalR...) sont ignorés au lieu de faire échouer la diffusion.
        var excluded = (record.Connections ?? [])
            .Select(id => Guid.TryParse(id, out var guid) ? guid : (Guid?)null)
            .OfType<Guid>()
            .ToList();

        IGroup<IRecordHubReceiver<TObject>>? group;
        lock (_gate)
        {
            group = _group;
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                foreach (var id in _subscribers.Where(id => !excluded.Contains(id)))
                    _logger.LogDebug("Notification de {Record} à {ContextId}", record, id);
            }
        }

        try
        {
            group?.Except(excluded).OnChanged(record);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Échec de la diffusion du changement {Record}.", record);
        }
    }

    private void OnRecordChanged(object? sender, Record<TObject> record) => Raise(record);

    public void Dispose()
    {
        _monitor.RecordChanged -= OnRecordChanged;
        _registry?.Remove(_monitor);
    }
}

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using System.Threading.Channels;

namespace Queryeables.SignalR.Server;

/// <summary>
/// Diffuse aux clients connectés les changements d'un type d'entité :
/// ceux de la base du serveur (via le moniteur) et ceux que les clients signalent.
/// À enregistrer en singleton.
/// </summary>
public sealed class ConnectionTracker<TObject> : IDisposable where TObject : class
{
    private readonly HashSet<string> _connections = new();
    private readonly object _gate = new();

    private readonly IHubContext<SignalRHub<TObject>> _hubContext;
    private readonly IDbMonitor<TObject> _monitor;
    private readonly DbMonitorRegistry? _registry;
    private readonly ILogger? _logger;

    private readonly Channel<Record<TObject>> _notifications =
        Channel.CreateUnbounded<Record<TObject>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _notificationLoop;
    private int _disposed;

    /// <param name="registry">Si fourni, le moniteur y est ajouté pour recevoir les changements de la base du serveur.</param>
    public ConnectionTracker(
        IHubContext<SignalRHub<TObject>> hubContext,
        IDbMonitor<TObject> monitor,
        DbMonitorRegistry? registry = null,
        ILogger<ConnectionTracker<TObject>>? logger = null)
    {
        _hubContext = hubContext;
        _monitor = monitor;
        _registry = registry;
        _logger = logger;

        _registry?.Add(monitor);
        _monitor.RecordChanged += OnRecordChanged;

        _notificationLoop = ProcessNotificationsAsync(_disposeCts.Token);
    }

    public void Add(string connectionId)
    {
        lock (_gate)
            _connections.Add(connectionId);

        _logger?.LogInformation("Client connecté : {ConnectionId}", connectionId);
    }

    public void Remove(string connectionId)
    {
        lock (_gate)
            _connections.Remove(connectionId);

        _logger?.LogInformation("Client déconnecté : {ConnectionId}", connectionId);
    }

    /// <summary>Met un changement en file pour diffusion à tous les clients, sauf ceux déjà au courant.</summary>
    public void Relay(Record<TObject> record)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (!_notifications.Writer.TryWrite(record))
            _logger?.LogWarning("Impossible de mettre en file la notification de changement.");
    }

    private void OnRecordChanged(object? sender, Record<TObject> record) => Relay(record);

    private async Task ProcessNotificationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var record in _notifications.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await BroadcastAsync(record, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Échec de la notification d'un changement aux clients.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "La boucle de notification s'est arrêtée.");
        }
    }

    private async Task BroadcastAsync(Record<TObject> record, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> excluded = record.Connections ?? [];

        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            lock (_gate)
            {
                foreach (var id in _connections.Where(id => !excluded.Contains(id)))
                    _logger.LogDebug("Notification de {Record} à {ConnectionId}", record, id);
            }
        }

        await _hubContext.Clients
            .AllExcept(excluded)
            .SendAsync(SignalRContract.DbChanged, record, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _monitor.RecordChanged -= OnRecordChanged;
        _registry?.Remove(_monitor);

        // Plus rien n'entre dans la file ; on laisse partir ce qui y est déjà.
        _notifications.Writer.TryComplete();
        try
        {
            _notificationLoop.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _disposeCts.Dispose();
        }
    }
}

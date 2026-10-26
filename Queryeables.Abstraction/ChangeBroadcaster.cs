using System.Threading.Channels;

namespace Queryeables.Abstraction;

/// <summary>
/// Côté serveur, commun à tous les transports (SignalR, MagicOnion...) : reçoit les changements d'un type d'entité,
/// ceux de la base via le moniteur et ceux que les clients signalent, et les diffuse aux clients abonnés,
/// sauf à ceux qui en sont déjà informés.
/// <para>
/// Les diffusions passent par une file : l'ordre est conservé, et la source de changements (hook SQLite...)
/// n'attend jamais le réseau. Un transport n'a qu'à implémenter <see cref="SendAsync"/>.
/// </para>
/// </summary>
public abstract class ChangeBroadcaster<TObject> : IDisposable where TObject : class
{
    private readonly HashSet<string> _subscribers = new();
    private readonly object _gate = new();
    private readonly IDbMonitor<TObject> _monitor;
    private readonly DbMonitorRegistry? _registry;

    private readonly Channel<Record<TObject>> _queue = Channel.CreateUnbounded<Record<TObject>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _loop;
    private int _disposed;

    /// <param name="registry">Si fourni, le moniteur y est ajouté pour recevoir les changements de la base du serveur.</param>
    protected ChangeBroadcaster(IDbMonitor<TObject> monitor, DbMonitorRegistry? registry = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _registry = registry;

        _registry?.Add(monitor);
        _monitor.RecordChanged += OnRecordChanged;
        _loop = ProcessAsync(_disposeCts.Token);
    }

    /// <summary>Connexions abonnées (copie).</summary>
    public IReadOnlyCollection<string> Subscribers
    {
        get { lock (_gate) return _subscribers.ToArray(); }
    }

    /// <returns>true si la connexion n'était pas déjà abonnée.</returns>
    public bool AddSubscriber(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (_gate)
            return _subscribers.Add(connectionId);
    }

    /// <returns>true si la connexion était abonnée.</returns>
    public bool RemoveSubscriber(string connectionId)
    {
        lock (_gate)
            return _subscribers.Remove(connectionId);
    }

    /// <summary>Met un changement en file pour diffusion (par exemple un changement signalé par un client).</summary>
    public void Relay(Record<TObject> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (!_queue.Writer.TryWrite(record))
            OnError(new InvalidOperationException($"Impossible de mettre en file le changement {record}."));
    }

    /// <summary>Envoie le changement à tous les clients, sauf <paramref name="excludedConnections"/>.</summary>
    protected abstract Task SendAsync(Record<TObject> record, IReadOnlyList<string> excludedConnections, CancellationToken ct);

    /// <summary>Appelé quand une diffusion échoue ; la diffusion des changements suivants continue.</summary>
    protected virtual void OnError(Exception exception)
    {
    }

    private void OnRecordChanged(object? sender, Record<TObject> record) => Relay(record);

    private async Task ProcessAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var record in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await SendAsync(record, record.Connections ?? [], ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnError(ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            OnError(ex);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || !disposing)
            return;

        _monitor.RecordChanged -= OnRecordChanged;
        _registry?.Remove(_monitor);

        // Plus rien n'entre dans la file ; ce qui y est déjà part encore.
        _queue.Writer.TryComplete();
        try
        {
            _loop.GetAwaiter().GetResult();
        }
        finally
        {
            _disposeCts.Dispose();
        }
    }
}

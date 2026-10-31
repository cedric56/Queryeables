using System.Threading.Channels;

namespace Queryeables.Abstraction;

/// <summary>
/// Server side, shared by every transport (SignalR, MagicOnion...): receives the changes of one entity type,
/// both those of the database (through the change feed) and those reported by clients, and broadcasts them to the
/// subscribed clients, except those already aware of them.
/// <para>
/// Broadcasts go through a queue: order is preserved, and the change source (SQLite hook...)
/// never waits for the network. A transport only has to implement <see cref="SendAsync"/>.
/// </para>
/// </summary>
public abstract class ChangeBroadcaster<TObject> : IDisposable where TObject : class
{
    private readonly HashSet<string> _subscribers = new();
    private readonly object _gate = new();
    private readonly IChangeFeed<TObject> _monitor;
    private readonly ChangeFeedRegistry? _registry;

    private readonly Channel<EntityChange<TObject>> _queue = Channel.CreateUnbounded<EntityChange<TObject>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _loop;
    private int _disposed;

    /// <summary>How long Dispose waits for the queued changes to go out.</summary>
    protected virtual TimeSpan DrainTimeout => TimeSpan.FromSeconds(5);

    /// <param name="registry">When provided, the change feed is added to it to receive the server database changes.</param>
    protected ChangeBroadcaster(IChangeFeed<TObject> monitor, ChangeFeedRegistry? registry = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _registry = registry;

        _registry?.Add(monitor);
        _monitor.ChangeReceived += OnChangeReceived;
        _loop = ProcessAsync(_disposeCts.Token);
    }

    /// <summary>Subscribed connections (snapshot).</summary>
    public IReadOnlyCollection<string> Subscribers
    {
        get { lock (_gate) return _subscribers.ToArray(); }
    }

    /// <returns>true if the connection was not already subscribed.</returns>
    public bool AddSubscriber(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (_gate)
            return _subscribers.Add(connectionId);
    }

    /// <returns>true if the connection was subscribed.</returns>
    public bool RemoveSubscriber(string connectionId)
    {
        lock (_gate)
            return _subscribers.Remove(connectionId);
    }

    /// <summary>Queues a change for broadcasting (for example a change reported by a client).</summary>
    public void Relay(EntityChange<TObject> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (!_queue.Writer.TryWrite(record))
            OnError(new InvalidOperationException($"Could not queue the change {record}."));
    }

    /// <summary>Sends the change to every client except <paramref name="excludedConnections"/>.</summary>
    protected abstract Task SendAsync(EntityChange<TObject> record, IReadOnlyList<string> excludedConnections, CancellationToken ct);

    /// <summary>Called when a broadcast fails; broadcasting of the following changes goes on.</summary>
    protected virtual void OnError(Exception exception)
    {
    }

    private void OnChangeReceived(object? sender, EntityChange<TObject> record) => Relay(record);

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

        _monitor.ChangeReceived -= OnChangeReceived;
        _registry?.Remove(_monitor);

        // Nothing enters the queue anymore; what is already in it still goes out, for a few seconds at most:
        // a client that does not read must not block the shutdown of the server.
        _queue.Writer.TryComplete();
        try
        {
            if (!_loop.Wait(DrainTimeout))
            {
                _disposeCts.Cancel();
                _loop.Wait(DrainTimeout);
            }
        }
        catch (AggregateException)
        {
            // Errors were already reported by OnError.
        }
        finally
        {
            _disposeCts.Dispose();
        }
    }
}

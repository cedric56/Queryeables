using System.Security.Claims;
using System.Threading.Channels;

namespace Queryeables.Abstraction;

/// <summary>
/// Server side, shared by every transport (SignalR, MagicOnion...): receives the changes of one entity type from the
/// database (through the change feed) and broadcasts them to the subscribed clients. The author of a write is told too:
/// its replica reads the row again, like every other.
/// <para>
/// Broadcasts go through a queue: order is preserved, and the change source (SQLite hook...)
/// never waits for the network. A transport only has to implement <see cref="SendAsync"/>.
/// </para>
/// <para>
/// The clients send nothing but their subscription; the queue is bounded, and <see cref="ChangeHubOptions.Visibility"/>
/// can keep a change from the users that may not read its row.
/// </para>
/// </summary>
public abstract class ChangeBroadcaster<TObject> : IDisposable, IAsyncDisposable where TObject : class
{
    private readonly Dictionary<string, ClaimsPrincipal?> _subscribers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly IChangeFeed<TObject> _monitor;
    private readonly ChangeFeedRegistry? _registry;

    private readonly Channel<EntityChange<TObject>> _queue;
    private readonly ChangeHubOptions _options;

    // Set when the queue was full: the next broadcast is a "synchronize" for everyone instead of what was dropped.
    private int _overflowed;

    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _loop;
    private int _disposed;

    /// <summary>How long Dispose waits for the queued changes to go out.</summary>
    protected virtual TimeSpan DrainTimeout => TimeSpan.FromSeconds(5);

    /// <param name="registry">When provided, the change feed is added to it to receive the server database changes.</param>
    /// <param name="options">What is accepted from clients; the defaults when null.</param>
    protected ChangeBroadcaster(IChangeFeed<TObject> monitor, ChangeFeedRegistry? registry = null, ChangeHubOptions? options = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _registry = registry;
        _options = options ?? new ChangeHubOptions();
        _queue = Channel.CreateBounded<EntityChange<TObject>>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,     // TryWrite fails instead of waiting: see Relay
        });

        _registry?.Add(monitor);
        _monitor.ChangeReceived += OnChangeReceived;
        _loop = ProcessAsync(_disposeCts.Token);
    }

    /// <summary>Subscribed connections (snapshot).</summary>
    public IReadOnlyCollection<string> Subscribers
    {
        get { lock (_gate) return _subscribers.Keys.ToArray(); }
    }

    /// <summary>
    /// Whether <paramref name="user"/> may subscribe: an authenticated user, or anyone with
    /// <see cref="ChangeHubOptions.AllowAnonymous"/>. The hubs refuse the others before <see cref="AddSubscriber"/>.
    /// </summary>
    public bool Admits(ClaimsPrincipal? user) => _options.AllowAnonymous || user?.Identity?.IsAuthenticated == true;

    /// <param name="user">The authenticated user of the connection, for <see cref="ChangeHubOptions.Visibility"/>.</param>
    /// <returns>true if the connection was not already subscribed.</returns>
    public bool AddSubscriber(string connectionId, ClaimsPrincipal? user = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (_gate)
            return _subscribers.TryAdd(connectionId, user);
    }

    /// <returns>true if the connection was subscribed.</returns>
    public bool RemoveSubscriber(string connectionId)
    {
        lock (_gate)
            return _subscribers.Remove(connectionId);
    }

    /// <summary>Queues a change of the server for broadcasting.</summary>
    public void Relay(EntityChange<TObject> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (!_queue.Writer.TryWrite(record))
            Interlocked.Exchange(ref _overflowed, 1);       // full: the clients will synchronize instead
    }

    /// <summary>Sends the change to every subscribed client.</summary>
    protected abstract Task SendAsync(EntityChange<TObject> record, CancellationToken ct);

    /// <summary>
    /// Sends the change to <paramref name="recipients"/> only (subscribers of this server that
    /// <see cref="ChangeHubOptions.Visibility"/> accepts): never to a connection it did not check, such as one of another
    /// server behind a backplane, or one that arrived after the check.
    /// </summary>
    protected abstract Task SendToAsync(EntityChange<TObject> record, IReadOnlyList<string> recipients, CancellationToken ct);

    /// <summary>Called when a broadcast fails; broadcasting of the following changes goes on.</summary>
    protected virtual void OnError(Exception exception)
    {
    }

    private void OnChangeReceived(object? sender, EntityChange<TObject> record) => Relay(record);

    private async Task ProcessAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var queued in _queue.Reader.ReadAllAsync(ct))
            {
                var record = queued;
                if (Interlocked.Exchange(ref _overflowed, 0) != 0)
                {
                    // Changes were dropped: what is still queued is dropped too, and every client synchronizes from the
                    // database (a change with no key), which covers them all.
                    while (_queue.Reader.TryRead(out _))
                    {
                    }
                    record = new EntityChange<TObject>(string.Empty, ChangeType.None);
                    OnError(new InvalidOperationException("The change queue was full: the clients synchronize instead."));
                }

                try
                {
                    if (await RecipientsAsync(record, ct) is { } recipients)
                        await SendToAsync(record, recipients, ct);
                    else
                        await SendAsync(record, ct);
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

    /// <summary>
    /// The connections allowed to hear of <paramref name="record"/> by <see cref="ChangeHubOptions.Visibility"/>; null without a rule, or for a change that names no row: everyone hears of it.
    /// </summary>
    private async Task<IReadOnlyList<string>?> RecipientsAsync(EntityChange<TObject> record, CancellationToken ct)
    {
        if (_options.Visibility is not { } visibility || string.IsNullOrEmpty(record.EntityKey))
            return null;

        KeyValuePair<string, ClaimsPrincipal?>[] subscribers;
        lock (_gate)
            subscribers = _subscribers.ToArray();
        var allowed = new List<string>();
        foreach (var (connection, user) in subscribers)
        {
            bool visible;
            try
            {
                visible = await visibility(new ChangeAudience(user, typeof(TObject), record.EntityKey, record.ChangeType), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                OnError(ex);
                visible = false;                // a rule that fails tells nothing
            }
            if (visible)
                allowed.Add(connection);
        }
        return allowed;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Stops like <see cref="Dispose()"/>, without blocking a thread while the queued changes go out (what a host calls
    /// for a singleton that implements it).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!Stop())
            return;
        try
        {
            try
            {
                await _loop.WaitAsync(DrainTimeout);
            }
            catch (TimeoutException)
            {
                _disposeCts.Cancel();
                await _loop.WaitAsync(DrainTimeout);
            }
        }
        catch (Exception)
        {
            // A sender that never returns (timeout), or errors already reported by OnError.
        }
        finally
        {
            _disposeCts.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>Unsubscribes and closes the queue; false when already stopped.</summary>
    private bool Stop()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return false;
        _monitor.ChangeReceived -= OnChangeReceived;
        _registry?.Remove(_monitor);
        // Nothing enters the queue anymore; what is already in it still goes out, for a few seconds at most:
        // a client that does not read must not block the shutdown of the server.
        _queue.Writer.TryComplete();
        return true;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || !Stop())
            return;
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

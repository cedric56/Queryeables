namespace Queryeables.Abstraction;

/// <summary>
/// Client side, shared by every transport (SignalR, MagicOnion...): publishes the changes received from the server
/// on the local monitor, and sends local changes to the server.
/// <para>
/// It ignores the changes this client reported itself, and never sends back to the server a change
/// that came from it: otherwise a change would bounce from client to client.
/// </para>
/// </summary>
public sealed class ChangeRelay<TObject> : IDisposable where TObject : class
{
    // True while a change received from the server is being published locally.
    private readonly AsyncLocal<bool> _receiving = new();
    private readonly DbMonitor<TObject> _monitor;
    private readonly Func<string?> _connectionId;
    private readonly Func<Record<TObject>, Task>? _send;
    private readonly Action<Exception>? _onError;
    private int _disposed;

    /// <param name="connectionId">Connection id of this client, or null when it is not connected.</param>
    /// <param name="send">Sends a local change to the server. When null, the relay only receives.</param>
    /// <param name="onError">Called when sending a change fails.</param>
    public ChangeRelay(DbMonitor<TObject> monitor, Func<string?> connectionId,
        Func<Record<TObject>, Task>? send = null, Action<Exception>? onError = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _connectionId = connectionId ?? throw new ArgumentNullException(nameof(connectionId));
        _send = send;
        _onError = onError;

        if (_send != null)
            _monitor.RecordChanged += OnLocalChange;
    }

    /// <summary>Change received from the server: published locally, unless it comes from this client.</summary>
    public void Receive(Record<TObject> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (Volatile.Read(ref _disposed) != 0 || record.Contains(_connectionId()))
            return;

        _receiving.Value = true;
        try
        {
            _monitor.Publish(record);
        }
        finally
        {
            _receiving.Value = false;
        }
    }

    // async void is required by the event: no exception may escape.
    private async void OnLocalChange(object? sender, Record<TObject> record)
    {
        if (_receiving.Value || _send is null)
            return;

        try
        {
            var connectionId = _connectionId();
            if (connectionId is null)
                return; // not connected: nothing to send

            if (!record.Contains(connectionId))
                record.Add(connectionId);

            await _send(record);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _send != null)
            _monitor.RecordChanged -= OnLocalChange;
    }
}

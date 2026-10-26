namespace Queryeables.Abstraction;

/// <summary>
/// Côté client, commun à tous les transports (SignalR, MagicOnion...) : publie sur le moniteur local les changements
/// reçus du serveur, et envoie au serveur les changements locaux.
/// <para>
/// Il ignore les changements que ce client a lui-même signalés, et ne renvoie jamais au serveur un changement
/// qui en vient : sans cela, un changement rebondirait de client en client.
/// </para>
/// </summary>
public sealed class ChangeRelay<TObject> : IDisposable where TObject : class
{
    // Vrai pendant la publication locale d'un changement reçu du serveur.
    private readonly AsyncLocal<bool> _receiving = new();
    private readonly DbMonitor<TObject> _monitor;
    private readonly Func<string?> _connectionId;
    private readonly Func<Record<TObject>, Task>? _send;
    private readonly Action<Exception>? _onError;
    private int _disposed;

    /// <param name="connectionId">Identifiant de connexion de ce client, ou null s'il n'est pas connecté.</param>
    /// <param name="send">Envoie un changement local au serveur. Si null, le relais ne fait que recevoir.</param>
    /// <param name="onError">Appelé si l'envoi d'un changement échoue.</param>
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

    /// <summary>Changement reçu du serveur : publié localement, sauf s'il vient de ce client.</summary>
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

    // async void imposé par l'événement : aucune exception ne doit en sortir.
    private async void OnLocalChange(object? sender, Record<TObject> record)
    {
        if (_receiving.Value || _send is null)
            return;

        try
        {
            var connectionId = _connectionId();
            if (connectionId is null)
                return; // pas connecté : rien à envoyer

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

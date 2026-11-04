using Queryeables.Abstraction;

namespace Queryeables.Onion;

/// <summary>
/// Receives changes from the MagicOnion hub and publishes them on the local change feed, except this client's own
/// changes (see <see cref="ChangeRelay{TObject}"/>).
/// </summary>
/// <param name="send">
/// If provided, local changes are sent to the hub, for example
/// <c>r => { hub.OnChangeReceived(ChangeMessage.From(r)); return Task.CompletedTask; }</c>.
/// </param>
public sealed partial class ChangeHubReceiver<TObject>(
    IChangeFeed<TObject> monitor,
    IChangeSession session,
    Func<EntityChange<TObject>, Task>? send = null,
    Action<Exception>? onError = null)
    : IChangeHubReceiver<TObject>, IDisposable where TObject : class
{
    private readonly ChangeRelay<TObject> _relay = new(monitor,
        () => session.TryGetSession(typeof(TObject), out var current) ? current.session : null,
        send, onError);

    public void OnChanged(ChangeMessage record) => _relay.Receive(record.ToChange<TObject>());

    public void Dispose() => _relay.Dispose();
}

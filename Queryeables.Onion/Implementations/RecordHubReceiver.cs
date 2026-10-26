using Queryeables.Abstraction;

namespace Queryeables.Onion;

/// <summary>
/// Receives changes from the MagicOnion hub and publishes them on the local monitor, except this client's own
/// changes (see <see cref="ChangeRelay{TObject}"/>).
/// </summary>
/// <param name="send">
/// If provided, local changes are sent to the hub, for example
/// <c>r => { hub.OnRecordChanged(RecordMessage.From(r)); return Task.CompletedTask; }</c>.
/// </param>
public sealed partial class RecordHubReceiver<TObject>(
    IDbMonitor<TObject> monitor,
    IDbSession session,
    Func<Record<TObject>, Task>? send = null,
    Action<Exception>? onError = null)
    : IRecordHubReceiver<TObject>, IDisposable where TObject : class
{
    private readonly ChangeRelay<TObject> _relay = new(monitor,
        () => session.TryGetSession(typeof(TObject), out var current) ? current.session : null,
        send, onError);

    public void OnChanged(RecordMessage record) => _relay.Receive(record.ToRecord<TObject>());

    public void Dispose() => _relay.Dispose();
}

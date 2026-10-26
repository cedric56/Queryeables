using Queryeables.Abstraction;

namespace Queryeables.Onion;

/// <summary>
/// Reçoit les changements du hub MagicOnion et les publie sur le moniteur local, sauf ceux de ce client
/// (voir <see cref="ChangeRelay{TObject}"/>).
/// </summary>
/// <param name="send">
/// Si fourni, les changements locaux sont envoyés au hub, par exemple <c>r => { hub.OnRecordChanged(r); return Task.CompletedTask; }</c>.
/// </param>
public sealed partial class RecordHubReceiver<TObject>(
    DbMonitor<TObject> monitor,
    IDbSession session,
    Func<Record<TObject>, Task>? send = null,
    Action<Exception>? onError = null)
    : IRecordHubReceiver<TObject>, IDisposable where TObject : class
{
    private readonly ChangeRelay<TObject> _relay = new(monitor,
        () => session.TryGetSession(typeof(TObject), out var current) ? current.session : null,
        send, onError);

    public void OnChanged(Record<TObject> record) => _relay.Receive(record);

    public void Dispose() => _relay.Dispose();
}

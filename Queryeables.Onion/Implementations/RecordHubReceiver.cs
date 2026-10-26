using Queryeables.Abstraction;

namespace Queryeables.Onion;

/// <summary>Reçoit les changements du hub et les relaie au moniteur local, sauf ceux de ce client.</summary>
public sealed partial class RecordHubReceiver<TObject>(DbMonitor<TObject> monitor, IDbSession session)
    : IRecordHubReceiver<TObject> where TObject : class
{
    public void OnChanged(Record<TObject> record)
    {
        if (session.TryGetSession(typeof(TObject), out var current) && record.Contains(current.session))
            return;

        monitor.Publish(record);
    }
}

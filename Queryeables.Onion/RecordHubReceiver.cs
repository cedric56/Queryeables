using Queryeables.Abstraction;

namespace Queryeables.Onion;

public partial class RecordHubReceiver<TObject>(IDbMonitor<TObject> monitor, IDBSession dBSession) :
    IRecordHubReceiver<TObject> where TObject : class, new()
{
    public void OnChanged(Record<TObject> record)
    {
        dBSession.TryGetSession(typeof(TObject), out var tuple);

        if (record.Contains(tuple.session))
            return;

        monitor?.OnChanged(record);
    }
}

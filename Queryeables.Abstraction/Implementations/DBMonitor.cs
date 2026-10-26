using System;

namespace Queryeables.Abstraction;

public abstract class DBMonitor : IDBMonitor
{
    public abstract void OnChanged(ChangeType type, string database, string table, long rowid, string? connectionId);
}

public class DBMonitor<TObject> :
    DBMonitor,
    IDBMonitor<TObject>
    where TObject : class, new()
{
    public event EventHandler<Record<TObject>>? OnRecordChanged;

    public void OnChanged(Record<TObject> record)
    {
        OnRecordChanged?.Invoke(this, record);
    }

    public override void OnChanged(ChangeType type, string database, string table, long rowid, string? connectionId)
    {
        if (table == typeof(TObject).Name)
        {
            var record = new Record<TObject>(rowid, type, !string.IsNullOrWhiteSpace(connectionId) ? [connectionId] : []);
            OnChanged(record);
        }
    }
}

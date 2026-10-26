namespace Queryeables.Abstraction;

public interface IDBMonitor
{
}

public interface IDBMonitor<T> : IDBMonitor where T : class, new()
{
    void OnChanged(Record<T> record);
    event EventHandler<Record<T>> OnRecordChanged;
}

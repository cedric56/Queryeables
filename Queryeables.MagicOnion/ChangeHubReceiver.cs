using Queryeables.Abstraction;

namespace Queryeables.MagicOnion;

/// <summary>Receives the changes of the MagicOnion hub and publishes them on the local change feed.</summary>
public sealed partial class ChangeHubReceiver<TObject>(IChangeFeed<TObject> monitor) : IChangeHubReceiver<TObject>, IDisposable
    where TObject : class
{
    private int _disposed;

    public void OnChanged(ChangeMessage record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (Volatile.Read(ref _disposed) == 0)
            monitor.Publish(record.ToChange<TObject>());
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

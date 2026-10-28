using Queryeables.Abstraction;
using TableDependencyCore.SqlClient;
using TableDependencyCore.SqlClient.Base.EventArgs;

namespace Queryeables.SqlDependency;

public static class SqlDependencyExtensions
{
    /// <summary>
    /// Watches the <typeparamref name="TObject"/> table with SQL Server (Service Broker)
    /// and publishes each change on the monitor.
    /// </summary>
    /// <param name="getKey">Entity key passed to subscribers (long, Guid, string, tuple...).</param>
    /// <param name="unsubscribe">Stops watching.</param>
    /// <param name="onError">Called when handling a change fails; watching goes on.</param>
    /// <remarks>
    /// SQL Server does not tell who made the change: the client that initiated a change
    /// is notified of it too.
    /// </remarks>
    public static SqlTableDependencyCore<TObject> AddTableDependency<TObject, TKey>(
        this IDbMonitor<TObject> monitor,
        string connectionString,
        Func<TObject, TKey> getKey,
        out Action unsubscribe,
        Action<Exception>? onError = null)
        where TObject : class, new()
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(getKey);

        var dependency = new SqlTableDependencyCore<TObject>(connectionString);
        dependency.OnChanged += OnChanged;
        dependency.Start();

        unsubscribe = () =>
        {
            dependency.OnChanged -= OnChanged;
            dependency.Stop();
        };

        return dependency;

        void OnChanged(object? sender, RecordChangedEventArgs<TObject> e)
        {
            try
            {
                // Both enums have the same names (None, Delete, Insert, Update).
                if (!Enum.TryParse<ChangeType>(e.ChangeType.ToString(), out var type) || type == ChangeType.None)
                    return;

                monitor.Publish(new DbRecord<TObject>(DbRecordKey.Format(getKey(e.Entity)), type, []));
            }
            catch (Exception ex)
            {
                // An exception here would surface in the library's watcher thread.
                onError?.Invoke(ex);
            }
        }
    }
}

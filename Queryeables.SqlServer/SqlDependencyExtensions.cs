using Queryeables.Abstraction;
using TableDependencyCore.SqlClient;
using TableDependencyCore.SqlClient.Base.EventArgs;

namespace Queryeables.SqlServer;

public static class SqlDependencyExtensions
{
    /// <summary>
    /// Surveille la table de <typeparamref name="TObject"/> avec SQL Server (Service Broker)
    /// et publie chaque changement sur le moniteur.
    /// </summary>
    /// <param name="getKey">Clé de l'entité transmise aux abonnés (long, Guid, string, tuple...).</param>
    /// <param name="unsubscribe">Arrête la surveillance.</param>
    /// <param name="onError">Appelé si le traitement d'un changement échoue ; la surveillance continue.</param>
    /// <remarks>
    /// SQL Server ne dit pas qui a fait la modification : le client à l'origine d'un changement
    /// en est aussi notifié.
    /// </remarks>
    public static SqlTableDependencyCore<TObject> AddTableDependency<TObject, TKey>(
        this DbMonitor<TObject> monitor,
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
                // Les deux énumérations ont les mêmes noms (None, Delete, Insert, Update).
                if (!Enum.TryParse<ChangeType>(e.ChangeType.ToString(), out var type) || type == ChangeType.None)
                    return;

                monitor.Publish(new Record<TObject>(RecordKey.Format(getKey(e.Entity)), type, []));
            }
            catch (Exception ex)
            {
                // Une exception ici remonterait dans le thread de surveillance de la bibliothèque.
                onError?.Invoke(ex);
            }
        }
    }
}

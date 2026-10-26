using System;
using TableDependencyCore.SqlClient;
using TableDependencyCore.SqlClient.Base.EventArgs;

namespace Avae.DAL;

public static class SqlDependencyExtensions
{
    public static SqlTableDependencyCore<TObject> AddTableDependency<TObject>(
        this DBMonitor<TObject> monitor, string connectionString,
        Func<TObject, long> getId,
        out Action unsuscribe)
        where TObject : class, new()
    {
        var sqlDependency = new SqlTableDependencyCore<TObject>(connectionString);
        sqlDependency.OnChanged += OnChanged;
        sqlDependency.Start();
        //monitor.IsRunning = true;
        unsuscribe = () =>
        {
            sqlDependency.OnChanged -= OnChanged;
            sqlDependency.Stop();
        };

        return sqlDependency;

        void OnChanged(object? sender, RecordChangedEventArgs<TObject> e)
        {
            var record = new Record<TObject>(getId(e.Entity), Enum.Parse<ChangeType>(e.ChangeType.ToString()), []);
            monitor.OnChanged(record);
        }
    }
}

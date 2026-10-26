using Dommel;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLitePCL;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;

namespace Queryeables.Abstraction;

public class DBSqliteFactory : DBFactory<SqliteConnection>
{
    private readonly bool isTransaction;
    private readonly object _recordsLock = new();
    private readonly List<Record> _pendingRecords = new();

    private void AddRecord(Record record)
    {
        lock (_recordsLock)
        {
            _pendingRecords.Add(record);
        }
    }

    public DBSqliteFactory(string connectionString, bool isTransaction = true, DBFactoryOptions? options = null, ILogger? logger = null)
        : base(connectionString, options, logger)
    {
        DommelMapper.AddSqlBuilder(typeof(DBLogConnection), new SqliteSqlBuilder());

        this.isTransaction = isTransaction;
    }

    public override DbConnection? CreateConnection()
    {
        var connection = (DBLogConnection)base.CreateConnection()!;
        var sqliteconnection = (SqliteConnection)connection.Inner;
        var records = new List<Record>();

        //Sqlite only raise database changes on current connection
        raw.sqlite3_commit_hook(sqliteconnection.Handle, (user_data) =>
        {
            if (isTransaction)
            {
                _ = Task.Run(RaiseMonitors);
            }
            return 0;

        }, null);

        raw.sqlite3_update_hook(sqliteconnection.Handle, (user_data, type, database, table, rowid) =>
        {
            AddRecord(new Record()
            {
                database = database,
                type = type switch
                {
                    9 => ChangeType.Delete,
                    18 => ChangeType.Insert,
                    23 => ChangeType.Update,
                    _ => ChangeType.None
                },
                rowid = rowid,
                table = table
            });

            if (!isTransaction)
            {
                RaiseMonitors();
            }

        }, null);

        void RaiseMonitors()
        {
            List<Record> snapshot;

            lock (_recordsLock)
            {
                if (_pendingRecords.Count == 0)
                    return;

                snapshot = new List<Record>(_pendingRecords);
                _pendingRecords.Clear();
            }

            foreach (var monitor in GetMonitors().OfType<DBMonitor>())
                foreach (var record in snapshot.DistinctBy(r => r.rowid))
                {
                    monitor.OnChanged(record.type, record.database, record.table, record.rowid, DBContext.CurrentConnectionId.Value);
                }
        }

        return connection;
    }
}

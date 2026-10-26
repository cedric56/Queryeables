using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using Queryeables.Dommel;
using Queryeables.Dommel.Logging;
using SQLitePCL;
using System.Data;
using System.Data.Common;

namespace Queryeables.Sqlite;

/// <summary>
/// SQLite connection factory that reports each modified row to the monitors of <see cref="DbMonitorRegistry"/>.
/// SQLite only reports changes on the connection that makes them: every connection created here is monitored.
/// </summary>
public class SqliteDbFactory : DbFactory<SqliteConnection>
{
    private const int SqliteDelete = 9;
    private const int SqliteInsert = 18;
    private const int SqliteUpdate = 23;

    private readonly DbMonitorRegistry _registry;
    private readonly bool _notifyOnCommit;

    /// <param name="isTransaction">
    /// true: changes are reported after the transaction commits, and forgotten on rollback.
    /// false: each row is reported as soon as it changes.
    /// </param>
    public SqliteDbFactory(string connectionString, DbMonitorRegistry registry, bool isTransaction = true,
        DbFactoryOptions? options = null, ILogger? logger = null)
        : base(connectionString, options, logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _notifyOnCommit = isTransaction;
    }

    public override DbConnection CreateConnection()
    {
        var connection = base.CreateConnection();
        var sqlite = connection as SqliteConnection
            ?? (SqliteConnection)((DbLogConnection)connection).Inner;

        // The connection is returned closed: hooks can only be set once the native handle is open.
        sqlite.StateChange += (_, e) =>
        {
            if (e.CurrentState == ConnectionState.Open)
                RegisterHooks(sqlite);
        };

        return connection;
    }

    private void RegisterHooks(SqliteConnection sqlite)
    {
        // Changes of this connection not reported yet (waiting for commit).
        var pending = new List<Change>();
        var gate = new object();
        var db = sqlite.Handle;
        if (db is null)
            return;

        raw.sqlite3_update_hook(db, (user_data, type, database, table, rowid) =>
        {
            string tableName = table;
            // The client that initiated the write is read now, during the write.
            var change = new Change(ToChangeType(type), tableName, rowid, ChangeContext.ConnectionId);

            if (_notifyOnCommit)
            {
                lock (gate)
                    pending.Add(change);
            }
            else
            {
                Publish([change]);
            }
        }, null);

        raw.sqlite3_commit_hook(db, user_data =>
        {
            List<Change> committed;
            lock (gate)
            {
                committed = [.. pending];
                pending.Clear();
            }

            // The hook runs before the commit completes: publish afterwards, outside the SQLite thread.
            if (committed.Count > 0)
                _ = Task.Run(() => Publish(committed));

            return 0; // 0 = let the commit proceed
        }, null);

        raw.sqlite3_rollback_hook(db, user_data =>
        {
            lock (gate)
                pending.Clear();
        }, null);
    }

    private void Publish(IReadOnlyList<Change> changes)
    {
        // A row modified several times is reported only once, with its last state.
        var latest = changes
            .GroupBy(c => (c.Table, c.RowId))
            .Select(g => g.Last());

        foreach (var change in latest)
        {
            try
            {
                _registry.Publish(change.Type, change.Table, change.RowId, change.ConnectionId);
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Échec de la notification du changement {Table}#{RowId}.", change.Table, change.RowId);
            }
        }
    }

    private static ChangeType ToChangeType(int operation) => operation switch
    {
        SqliteDelete => ChangeType.Delete,
        SqliteInsert => ChangeType.Insert,
        SqliteUpdate => ChangeType.Update,
        _ => ChangeType.None
    };

    private readonly record struct Change(ChangeType Type, string Table, long RowId, string? ConnectionId);
}

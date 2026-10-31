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
/// SQLite connection factory that reports each modified row to the change feeds of <see cref="ChangeFeedRegistry"/>.
/// SQLite only reports changes on the connection that makes them: every connection created here is monitored.
/// </summary>
public class SqliteDbFactory : DbFactory<SqliteConnection>
{
    private const int SqliteDelete = 9;
    private const int SqliteInsert = 18;
    private const int SqliteUpdate = 23;

    private readonly ChangeFeedRegistry _registry;
    private readonly bool _notifyOnCommit;

    /// <param name="isTransaction">
    /// true: changes are reported after the transaction commits, and forgotten on rollback.
    /// false: each row is reported as soon as it changes.
    /// </param>
    public SqliteDbFactory(string connectionString, ChangeFeedRegistry registry, bool isTransaction = true,
        DbFactoryOptions? options = null, ILogger? logger = null)
        : base(connectionString, options, logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _notifyOnCommit = isTransaction;
    }

    /// <summary>
    /// How long a commit made outside <see cref="DommelEntityStore"/> (Dapper on the connection...) waits before its changes
    /// are reported. Stores report theirs as soon as the commit returns (<see cref="CommittedWrites"/>).
    /// </summary>
    public TimeSpan UnsignaledCommitDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    public override DbConnection CreateConnection()
    {
        var connection = base.CreateConnection();
        var sqlite = connection as SqliteConnection
            ?? (SqliteConnection)((DbLogConnection)connection).Inner;
        var changes = new ConnectionChanges();

        // The connection is returned closed: hooks can only be set once the native handle is open.
        sqlite.StateChange += (_, e) =>
        {
            if (e.CurrentState == ConnectionState.Open)
                RegisterHooks(sqlite, changes);
        };

        // The commit hook runs before the commit completes: a subscriber reading the row at that moment could still see
        // its previous state. Committed changes are reported once the store says the commit returned.
        // Off the writer's thread: the caches that read the row again do not delay the write that triggered them.
        CommittedWrites.OnCommitted(connection, () => _ = Task.Run(() => PublishCommitted(changes)));
        return connection;
    }

    /// <summary>The changes of one connection, waiting for their commit, then for their report.</summary>
    private sealed class ConnectionChanges
    {
        public readonly object Gate = new();
        public readonly List<Change> Pending = [];
        public readonly List<Change> Committed = [];
    }

    private void PublishCommitted(ConnectionChanges changes)
    {
        List<Change> committed;
        lock (changes.Gate)
        {
            if (changes.Committed.Count == 0)
                return;
            committed = [.. changes.Committed];
            changes.Committed.Clear();
        }
        Publish(committed);
    }

    private void RegisterHooks(SqliteConnection sqlite, ConnectionChanges changes)
    {
        var pending = changes.Pending;
        var gate = changes.Gate;
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
            bool any;
            lock (gate)
            {
                changes.Committed.AddRange(pending);
                pending.Clear();
                any = changes.Committed.Count > 0;
            }

            // Reported when the store signals the end of the commit; otherwise (a commit made without the store),
            // after a short delay, outside the SQLite thread.
            if (any)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(UnsignaledCommitDelay);
                    PublishCommitted(changes);
                });

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
                Logger?.LogError(ex, "Failed to report the change {Table}#{RowId}.", change.Table, change.RowId);
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

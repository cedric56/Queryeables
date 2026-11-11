using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using Queryeables.Dommel;
using Queryeables.Dommel.Logging;
using SQLitePCL;
using System.Collections.Concurrent;
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

    /// <summary>
    /// A commit made outside <see cref="DommelEntityStore"/> is reported again after this delay: if it took longer than
    /// <see cref="UnsignaledCommitDelay"/> (a slow disk), the first report was read before the commit, the second after.
    /// </summary>
    public TimeSpan UnsignaledCommitRecheck { get; init; } = TimeSpan.FromSeconds(1);

    // Whether the rowid of a table is its key (an INTEGER PRIMARY KEY): only then is a change reported with its key.
    private readonly ConcurrentDictionary<string, bool> _rowIdIsKey = new(StringComparer.OrdinalIgnoreCase);

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

    /// <returns>The changes reported, or null when there were none (the store reported them first).</returns>
    private List<Change>? PublishCommitted(ConnectionChanges changes)
    {
        List<Change> committed;
        lock (changes.Gate)
        {
            if (changes.Committed.Count == 0)
                return null;
            committed = [.. changes.Committed];
            changes.Committed.Clear();
        }
        Publish(committed);
        return committed;
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
                // Off the SQLite thread: the report may read the schema (RowIdIsKey) on another connection.
                _ = Task.Run(() => Publish([change]));
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
            // after a short delay, outside the SQLite thread, and once more later in case the commit was not over yet.
            if (any)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(UnsignaledCommitDelay);
                    if (PublishCommitted(changes) is not { } unsignaled)
                        return;
                    await Task.Delay(UnsignaledCommitRecheck);
                    Publish(unsignaled);
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
                // The rowid is the key of an INTEGER PRIMARY KEY table only. Otherwise ("Id INT PRIMARY KEY", a text key...)
                // the change is reported without a key: the caches of the table synchronize instead of reading another row.
                var key = RowIdIsKey(change.Table) ? EntityKey.Format(change.RowId) : string.Empty;
                _registry.Publish(change.Type, change.Table, key, change.ConnectionId);
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to report the change {Table}#{RowId}.", change.Table, change.RowId);
            }
        }
    }

    /// <summary>
    /// Whether the table's primary key is its rowid: a single column declared exactly INTEGER (an alias of the rowid). Read
    /// once per table, on a connection of its own (never inside a SQLite hook). A table the probe cannot see (a private
    /// in-memory database) keeps the rowid, as before; a WITHOUT ROWID table reports nothing to the hooks at all.
    /// </summary>
    private bool RowIdIsKey(string table)
    {
        if (_rowIdIsKey.TryGetValue(table, out var known))
            return known;
        try
        {
            using var connection = base.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
            var keys = new List<string>();
            var columns = 0;
            using (var reader = command.ExecuteReader())
            {
                // cid, name, type, notnull, dflt_value, pk
                while (reader.Read())
                {
                    columns++;
                    if (reader.GetInt64(5) > 0)
                        keys.Add(reader.GetString(2));
                }
            }
            if (columns == 0)
                return true;
            var isKey = keys is [var type] && string.Equals(type.Trim(), "INTEGER", StringComparison.OrdinalIgnoreCase);
            if (!isKey)
                Logger?.LogInformation("The key of {Table} is not its rowid: its changes are reported without a key, and its caches synchronize.", table);
            return _rowIdIsKey[table] = isKey;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            Logger?.LogWarning(ex, "The key of {Table} could not be read: its changes are reported without a key.", table);
            return false;
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

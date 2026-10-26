using Dommel;
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
/// Fabrique de connexions SQLite qui signale chaque ligne modifiée aux moniteurs de <see cref="DbMonitorRegistry"/>.
/// SQLite ne signale les changements que sur la connexion qui les fait : chaque connexion créée ici est surveillée.
/// </summary>
public class SqliteDbFactory : DbFactory<SqliteConnection>
{
    private const int SqliteDelete = 9;
    private const int SqliteInsert = 18;
    private const int SqliteUpdate = 23;

    private readonly DbMonitorRegistry _registry;
    private readonly bool _notifyOnCommit;

    /// <param name="isTransaction">
    /// true : les changements sont signalés après la validation de la transaction, et oubliés en cas d'annulation.
    /// false : chaque ligne est signalée dès qu'elle change.
    /// </param>
    public SqliteDbFactory(string connectionString, DbMonitorRegistry registry, bool isTransaction = true,
        DbFactoryOptions? options = null, ILogger? logger = null)
        : base(connectionString, options, logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _notifyOnCommit = isTransaction;

        // Dialecte SQLite pour la connexion journalisée, sans dépendre de la détection automatique.
        DommelMapper.AddSqlBuilder(typeof(DbLogConnection), new SqliteSqlBuilder());
    }

    public override DbConnection CreateConnection()
    {
        var connection = base.CreateConnection();
        var sqlite = connection as SqliteConnection
            ?? (SqliteConnection)((DbLogConnection)connection).Inner;

        // La connexion est rendue fermée : les hooks ne peuvent être posés qu'une fois le handle natif ouvert.
        sqlite.StateChange += (_, e) =>
        {
            if (e.CurrentState == ConnectionState.Open)
                RegisterHooks(sqlite);
        };

        return connection;
    }

    private void RegisterHooks(SqliteConnection sqlite)
    {
        // Changements de cette connexion pas encore signalés (en attente de validation).
        var pending = new List<Change>();
        var gate = new object();
        var db = sqlite.Handle;
        if (db is null)
            return;

        raw.sqlite3_update_hook(db, (user_data, type, database, table, rowid) =>
        {
            string tableName = table;
            // Le client à l'origine de l'écriture est lu maintenant, pendant l'écriture.
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

            // Le hook s'exécute avant la fin de la validation : on publie après, hors du thread SQLite.
            if (committed.Count > 0)
                _ = Task.Run(() => Publish(committed));

            return 0; // 0 = laisser la validation se faire
        }, null);

        raw.sqlite3_rollback_hook(db, user_data =>
        {
            lock (gate)
                pending.Clear();
        }, null);
    }

    private void Publish(IReadOnlyList<Change> changes)
    {
        // Une ligne modifiée plusieurs fois n'est signalée qu'une fois, avec son dernier état.
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

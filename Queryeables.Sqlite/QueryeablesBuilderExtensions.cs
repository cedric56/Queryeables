using Microsoft.Extensions.Logging;
using Queryeables.Dommel;

namespace Queryeables.Sqlite;

public static class QueryeablesBuilderExtensions
{
    /// <summary>
    /// Stores with Dommel on SQLite. Every row changed through these stores is reported to the change feeds
    /// (see <see cref="SqliteDbFactory"/>): the caches follow the database.
    /// </summary>
    /// <param name="isTransaction">True: changes are reported after the commit, and forgotten on rollback.</param>
    /// <param name="logCommands">Logs every SQL command (category "Queryeables.SQL"). For development.</param>
    /// <param name="createDb">Called once with the factory, for example to create the tables.</param>
    public static QueryeablesBuilder UseSqlite(this QueryeablesBuilder builder, string connectionString,
        bool isTransaction = true, bool logCommands = false, LogLevel commandLogLevel = LogLevel.Information,
        Action<SqliteDbFactory>? createDb = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.UseSqliteFactory(connectionString, isTransaction, logCommands, commandLogLevel, createDb);
        return builder.UseDommelStores();
    }
}

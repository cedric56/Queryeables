using Dommel;
using Microsoft.Extensions.Logging;
using Queryeables.Dommel.Logging;
using System.Data.Common;

namespace Queryeables.Dommel;

/// <summary>Options de <see cref="DbFactory{TDbConnection}"/>.</summary>
public sealed class DbFactoryOptions
{
    /// <summary>
    /// Si true, chaque commande SQL est journalisée. À réserver au développement.
    /// </summary>
    public bool LogCommands { get; set; }

    /// <summary>Niveau de log des lignes SQL. Par défaut : Information.</summary>
    public LogLevel CommandLogLevel { get; set; } = LogLevel.Information;
}

/// <summary>
/// Fabrique de connexions ADO.NET préconfigurées (chaîne de connexion, journalisation SQL).
/// Les connexions sont renvoyées fermées : c'est le store qui les ouvre.
/// </summary>
/// <example>
/// <code>
/// var factory = new DbFactory&lt;SqliteConnection&gt;(connectionString);
/// var cache = new EntityCache&lt;Client, long&gt;(() => DommelEntityStore.Create(factory), c => c.Id);
/// </code>
/// </example>
public class DbFactory<TDbConnection> : DbProviderFactory where TDbConnection : DbConnection, new()
{
    private readonly string _connectionString;
    private readonly ILogger? _logger;
    private readonly LogLevel _logLevel;

    public DbFactory(string connectionString, DbFactoryOptions? options = null, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _logLevel = options?.CommandLogLevel ?? LogLevel.Information;

        if (options?.LogCommands == true && logger != null)
        {
            _logger = logger;

            // Dommel choisit son dialecte SQL d'après le type de la connexion. Sans cela, il ne reconnaîtrait
            // pas DbLogConnection et produirait du SQL Server, même sur SQLite.
            using var probe = new TDbConnection();
            DommelMapper.AddSqlBuilder(typeof(DbLogConnection), DommelMapper.GetSqlBuilder(probe));
        }
    }

    public override DbConnection CreateConnection()
    {
        var connection = new TDbConnection { ConnectionString = _connectionString };
        return _logger is null ? connection : new DbLogConnection(connection, _logger, _logLevel);
    }
}

using Microsoft.Extensions.Logging;
using Queryeables.Dommel.Logging;
using System.Data.Common;

namespace Queryeables.Dommel;

/// <summary>Options of <see cref="DbFactory{TDbConnection}"/>.</summary>
public sealed class DbFactoryOptions
{
    /// <summary>
    /// When true, every SQL command is logged. Meant for development only.
    /// </summary>
    public bool LogCommands { get; set; }

    /// <summary>Log level of the SQL lines. Default: Information.</summary>
    public LogLevel CommandLogLevel { get; set; } = LogLevel.Information;
}

/// <summary>
/// Factory of preconfigured ADO.NET connections (connection string, SQL logging).
/// Connections are returned closed: the store opens them.
/// </summary>
/// <example>
/// <code>
/// var factory = new DbFactory&lt;SqliteConnection&gt;(connectionString);
/// var cache = new EntityCache&lt;Customer, long&gt;(() => DommelEntityStore.Create(factory), c => c.Id);
/// </code>
/// </example>
public class DbFactory<TDbConnection> : DbProviderFactory where TDbConnection : DbConnection, new()
{
    private readonly string _connectionString;
    private readonly bool _logCommands;
    private readonly LogLevel _logLevel;

    /// <summary>Logger given to the constructor, available to derived classes.</summary>
    protected ILogger? Logger { get; }

    public DbFactory(string connectionString, DbFactoryOptions? options = null, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _logLevel = options?.CommandLogLevel ?? LogLevel.Information;
        Logger = logger;
        _logCommands = options?.LogCommands == true && logger != null;

        if (_logCommands)
            DbLogConnection.UseDialectOf<TDbConnection>();
    }

    public override DbConnection CreateConnection()
    {
        var connection = new TDbConnection { ConnectionString = _connectionString };
        return _logCommands ? new DbLogConnection(connection, Logger!, _logLevel) : connection;
    }
}

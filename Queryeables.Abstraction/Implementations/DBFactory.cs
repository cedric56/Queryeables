using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;

namespace Queryeables.Abstraction;

/// <summary>
/// Options for <see cref="DBFactory{TDbConnection}"/>.
/// </summary>
public sealed class DBFactoryOptions
{
    /// <summary>
    /// When true, <see cref="IDBFactory.CreateConnection"/> wraps connections so each command is logged.
    /// Prefer enabling only in Development.
    /// </summary>
    public bool LogCommands { get; set; }

    /// <summary>Log level used for SQL lines. Default: Information.</summary>
    public LogLevel CommandLogLevel { get; set; } = LogLevel.Information;
}

public class DBFactory<TDbConnection>(
    string connectionString,
    DBFactoryOptions? options = null,
    ILogger? logger = null) : DbProviderFactory,
    IDBSession
    where TDbConnection : DbConnection, new()
{
    private readonly ConcurrentDictionary<IDBMonitor, byte> _monitors = new();
    private readonly ConcurrentDictionary<Type, (string session, Func<Task> unsubscribe)> _sessions = new();

    public void AddMonitor(IDBMonitor monitor)
    {
        _monitors.TryAdd(monitor, 0);
    }

    public override DbConnection? CreateConnection()
    {
        var connection = new TDbConnection()
        {
            ConnectionString = connectionString
        };
        connection.Open();
        if (options == null || !options.LogCommands || logger is null)
            return connection;

        return new DBLogConnection(connection, logger, options.CommandLogLevel);
    }

    public IReadOnlyList<IDBMonitor> GetMonitors()
        => _monitors.Keys.ToArray();

    public bool RemoveMonitor(IDBMonitor monitor)
        => _monitors.TryRemove(monitor, out _);

    public bool RemoveSession(Type type)
        => _sessions.TryRemove(type, out _);

    public void SetSession(Type type, (string session, Func<Task> unsubscribe) session)
        => _sessions[type] = session;

    public bool TryGetSession(Type type, out (string session, Func<Task> unsubscribe) session)
        => _sessions.TryGetValue(type, out session!);
}

using Dommel;
using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Queryeables.Dommel.Logging;

/// <summary>
/// <see cref="DbConnection"/> decorator that returns <see cref="DbLogCommand"/> instances.
/// Created by <see cref="DbFactory{TDbConnection}"/> when command logging is enabled.
/// </summary>
public sealed class DbLogConnection : DbConnection
{
    /// <summary>Actual provider connection (SqliteConnection, SqlConnection...).</summary>
    public DbConnection Inner { get; }

    private static readonly object _dialectGate = new();
    private static Type? _dialectProvider;

    /// <summary>
    /// Tells Dommel the SQL dialect of logged connections: the one of <typeparamref name="TDbConnection"/>.
    /// </summary>
    /// <remarks>
    /// Dommel picks its dialect from the connection type, an application-wide setting.
    /// All logged connections share the same type, so only one provider can be logged at a time:
    /// a second, different provider throws, instead of silently receiving the wrong SQL.
    /// </remarks>
    public static void UseDialectOf<TDbConnection>() where TDbConnection : DbConnection, new()
    {
        lock (_dialectGate)
        {
            if (_dialectProvider == typeof(TDbConnection))
                return;

            if (_dialectProvider is not null)
                throw new InvalidOperationException(
                    $"La journalisation SQL est déjà active pour {_dialectProvider.Name} : " +
                    $"elle ne peut pas l'être aussi pour {typeof(TDbConnection).Name}. " +
                    "Désactivez LogCommands pour l'un des deux fournisseurs.");

            using var probe = new TDbConnection();
            DommelMapper.AddSqlBuilder(typeof(DbLogConnection), DommelMapper.GetSqlBuilder(probe));
            _dialectProvider = typeof(TDbConnection);
        }
    }
    private readonly ILogger _logger;
    private readonly LogLevel _level;
    private bool _disposed;

    public DbLogConnection(
        DbConnection inner,
        ILogger logger,
        LogLevel level = LogLevel.Information)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _level = level;
    }

    protected override DbCommand CreateDbCommand()
        => new DbLogCommand(Inner.CreateCommand(), _logger, _level);

    [AllowNull]
    public override string ConnectionString
    {
        get => Inner.ConnectionString;
        set => Inner.ConnectionString = value;
    }

    public override string Database => Inner.Database;
    public override string DataSource => Inner.DataSource;
    public override string ServerVersion => Inner.ServerVersion;
    public override ConnectionState State => Inner.State;

    public override void ChangeDatabase(string databaseName) => Inner.ChangeDatabase(databaseName);
    public override void Close() => Inner.Close();
    public override void Open() => Inner.Open();

    public override Task OpenAsync(CancellationToken cancellationToken)
        => Inner.OpenAsync(cancellationToken);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => Inner.BeginTransaction(isolationLevel);

    public override void EnlistTransaction(System.Transactions.Transaction? transaction)
        => Inner.EnlistTransaction(transaction);

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
            Inner.Dispose();

        _disposed = true;
        base.Dispose(disposing);
    }
}
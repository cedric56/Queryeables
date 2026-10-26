using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Queryeables.Abstraction;

/// <summary>
/// <see cref="DbConnection"/> decorator that returns <see cref="DBLogCommand"/> instances.
/// Created by <see cref="DBFactory{TDbConnection}"/> when command logging is enabled.
/// </summary>
internal sealed class DBLogConnection : DbConnection
{
    public DbConnection Inner { get; private set; }
    private readonly ILogger _logger;
    private readonly LogLevel _level;
    private bool _disposed;

    public DBLogConnection(
        DbConnection inner,
        ILogger logger,
        LogLevel level = LogLevel.Information)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _level = level;
    }

    protected override DbCommand CreateDbCommand()
        => new DBLogCommand(Inner.CreateCommand(), _logger, _level);

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
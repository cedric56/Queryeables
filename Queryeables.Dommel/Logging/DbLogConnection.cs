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
    /// <summary>Connexion réelle du fournisseur (SqliteConnection, SqlConnection...).</summary>
    public DbConnection Inner { get; }

    private static readonly object _dialectGate = new();
    private static Type? _dialectProvider;

    /// <summary>
    /// Indique à Dommel le dialecte SQL des connexions journalisées : celui de <typeparamref name="TDbConnection"/>.
    /// </summary>
    /// <remarks>
    /// Dommel choisit son dialecte d'après le type de la connexion, réglage global à l'application.
    /// Toutes les connexions journalisées ont le même type, donc un seul fournisseur peut être journalisé à la fois :
    /// un second fournisseur différent lève une exception, au lieu de recevoir silencieusement le mauvais SQL.
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
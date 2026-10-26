using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Queryeables.Dommel.Logging;

/// <summary>
/// <see cref="DbCommand"/> decorator that logs CommandText (with inlined input parameters)
/// before each execute. Intended for development diagnostics only.
/// </summary>
internal sealed class DbLogCommand : DbCommand
{
    private readonly DbCommand _inner;
    private readonly ILogger _logger;
    private readonly LogLevel _level;
    private bool _disposed;

    public DbLogCommand(
        DbCommand inner,
        ILogger logger,
        LogLevel level = LogLevel.Information)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _level = level;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _inner.CommandText;
        set => _inner.CommandText = value;
    }

    public override int CommandTimeout
    {
        get => _inner.CommandTimeout;
        set => _inner.CommandTimeout = value;
    }

    public override CommandType CommandType
    {
        get => _inner.CommandType;
        set => _inner.CommandType = value;
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => _inner.UpdatedRowSource;
        set => _inner.UpdatedRowSource = value;
    }

    public override bool DesignTimeVisible
    {
        get => _inner.DesignTimeVisible;
        set => _inner.DesignTimeVisible = value;
    }

    protected override DbConnection? DbConnection
    {
        get => _inner.Connection;
        set => _inner.Connection = value;
    }

    protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => _inner.Transaction;
        set => _inner.Transaction = value;
    }

    public override void Cancel() => _inner.Cancel();

    public override void Prepare() => _inner.Prepare();

    protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

    public override int ExecuteNonQuery()
    {
        LogBeforeExecute();
        return _inner.ExecuteNonQuery();
    }

    public override object? ExecuteScalar()
    {
        LogBeforeExecute();
        return _inner.ExecuteScalar();
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        LogBeforeExecute();
        return _inner.ExecuteReader(behavior);
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        LogBeforeExecute();
        return _inner.ExecuteNonQueryAsync(cancellationToken);
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        LogBeforeExecute();
        return _inner.ExecuteScalarAsync(cancellationToken);
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        LogBeforeExecute();
        return _inner.ExecuteReaderAsync(behavior, cancellationToken);
    }

    private void LogBeforeExecute()
    {
        if (!_logger.IsEnabled(_level))
            return;

        var sql = _inner.CommandText ?? string.Empty;
        foreach (IDataParameter parameter in _inner.Parameters)
        {
            if (parameter.Direction is ParameterDirection.Output or ParameterDirection.ReturnValue)
                continue;

            var name = parameter.ParameterName;
            if (string.IsNullOrEmpty(name))
                continue;

            var token = name[0] is '@' or ':' or '?' ? name : "@" + name;
            sql = ReplaceParameter(sql, token, FormatValue(parameter.Value));
        }

        _logger.Log(_level, "SQL: {Sql}", sql);
    }

    /// <summary>
    /// Remplace le paramètre seulement en mot entier : @Id ne doit pas toucher @IdClient.
    /// </summary>
    private static string ReplaceParameter(string sql, string token, string value)
        => Regex.Replace(sql, $@"(?<![\w@:?]){Regex.Escape(token)}(?!\w)", _ => value);

    private static string FormatValue(object? value)
    {
        if (value is null or DBNull)
            return "NULL";

        return value switch
        {
            string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
            DateTime dt => "'" + dt.ToString("O", CultureInfo.InvariantCulture) + "'",
            DateTimeOffset dto => "'" + dto.ToString("O", CultureInfo.InvariantCulture) + "'",
            bool b => b ? "1" : "0",
            Guid g => "'" + g.ToString("D") + "'",
            byte[] => "<byte[]>",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL"
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
            _inner.Dispose();

        _disposed = true;
        base.Dispose(disposing);
    }
}
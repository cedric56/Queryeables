using Microsoft.Data.Sqlite;
using Queryeables.Remote.Offline;
using System.Data.Common;

namespace Queryeables.Sqlite;

/// <summary>
/// The offline copy and its queue in a SQLite file: for desktop and MAUI applications, without the size limit of the
/// browser and without one file per row. One table, <c>queryeables_offline(key, value)</c>, created on first use; the
/// database can be the application's own or a file of its own.
/// </summary>
/// <example>
/// <code>
/// q.WithOfflineMode(o => o
///     .UseStorage(new SqliteOfflineStorage($"Data Source={Path.Combine(FileSystem.AppDataDirectory, "offline.db")}"))
///     .Entity&lt;Person, long&gt;(p => p.Id));
/// </code>
/// </example>
public sealed class SqliteOfflineStorage : IOfflineStorage
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schema = new(1, 1);
    private bool _ready;

    /// <param name="connectionString">For example <c>Data Source=offline.db</c>.</param>
    public SqliteOfflineStorage(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async ValueTask<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, "SELECT value FROM queryeables_offline WHERE key = $key", key);
        return await command.ExecuteScalarAsync(ct) as byte[];
    }

    public async ValueTask WriteAsync(string key, byte[] value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection,
            "INSERT INTO queryeables_offline (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            key, value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, "DELETE FROM queryeables_offline WHERE key = $key", key);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            if (!_ready)
                await EnsureSchemaAsync(connection, ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>The table, and WAL: a download writes one entry per row, which a journal per statement would make slow.</summary>
    private async Task EnsureSchemaAsync(DbConnection connection, CancellationToken ct)
    {
        await _schema.WaitAsync(ct);
        try
        {
            if (_ready)
                return;
            await using var command = Command(connection, """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                CREATE TABLE IF NOT EXISTS queryeables_offline (key TEXT PRIMARY KEY NOT NULL, value BLOB NOT NULL) WITHOUT ROWID;
                """);
            await command.ExecuteNonQueryAsync(ct);
            _ready = true;
        }
        finally
        {
            _schema.Release();
        }
    }

    private static DbCommand Command(DbConnection connection, string sql, string? key = null, byte[]? value = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (key is not null)
            Add(command, "$key", key);
        if (value is not null)
            Add(command, "$value", value);
        return command;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

using Microsoft.Data.Sqlite;
using Queryeables.Abstraction;
using Queryeables.Sqlite;

namespace Queryeables.Tests.Contract;

/// <summary>
/// The SQLite change source (<see cref="SqliteDbFactory"/>): a change is reported with the row's key only when the rowid is
/// that key (an INTEGER PRIMARY KEY); otherwise without a key, so that a cache synchronizes instead of reading another row.
/// </summary>
public sealed class SqliteChangeSourceTests : IAsyncLifetime
{
    public sealed class KeyedRow;

    public sealed class UnaliasedRow;

    private readonly string _connectionString = $"Data Source=changes_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection? _keepAlive;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();
        await using var command = _keepAlive.CreateCommand();
        command.CommandText = """
            CREATE TABLE Keyed (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);
            CREATE TABLE Unaliased (Id INT PRIMARY KEY, Name TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_keepAlive is not null)
            await _keepAlive.DisposeAsync();
    }

    private static Task<EntityChange<T>> NextChange<T>(ChangeFeed<T> feed) where T : class
    {
        var next = new TaskCompletionSource<EntityChange<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.ChangeReceived += (_, change) => next.TrySetResult(change);
        return next.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private async Task ExecuteAsync(SqliteDbFactory factory, string sql)
    {
        await using var connection = factory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_change_carries_the_key_when_the_rowid_is_the_key_and_no_key_otherwise()
    {
        var registry = new ChangeFeedRegistry();
        var keyed = new ChangeFeed<KeyedRow>("Keyed");
        var unaliased = new ChangeFeed<UnaliasedRow>("Unaliased");
        registry.Add(keyed);
        registry.Add(unaliased);
        var factory = new SqliteDbFactory(_connectionString, registry);

        var keyedChange = NextChange(keyed);
        await ExecuteAsync(factory, "INSERT INTO Keyed (Id, Name) VALUES (7, 'a')");
        Assert.Equal(EntityKey.Format(7L), (await keyedChange).EntityKey);

        // "Id INT PRIMARY KEY" is not the rowid: the row of Id 42 has the rowid 1, which must not be reported as its key.
        var unaliasedChange = NextChange(unaliased);
        await ExecuteAsync(factory, "INSERT INTO Unaliased (Id, Name) VALUES (42, 'b')");
        Assert.Equal(string.Empty, (await unaliasedChange).EntityKey);
    }
}

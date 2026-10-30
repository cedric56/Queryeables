using Dapper;
using MessagePack;
using Microsoft.Data.Sqlite;
using Queryeables.Abstraction;
using Queryeables.Dommel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;

namespace Queryeables.Benchmarks;

/// <summary>The measured entity: an integer key and two columns.</summary>
[Table("Item")]
[MessagePackObject]
public sealed partial class Item
{
    [Key(0)] public long Id { get; set; }
    [Key(1)] public string Name { get; set; } = string.Empty;
    [Key(2)] public int Quantity { get; set; }
}

/// <summary>
/// An in-memory SQLite database shared by the connections of one benchmark class, kept alive by an open connection.
/// No change hooks: the stores are measured, not the notifications.
/// </summary>
public sealed class BenchmarkDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _keeper;

    private BenchmarkDatabase(string name)
    {
        ConnectionString = $"Data Source={name};Mode=Memory;Cache=Shared";
        Factory = new DbFactory<SqliteConnection>(ConnectionString);
        Stores = new EntityStoreFactory(() => DommelEntityStore.Create(Factory));
        _keeper = new SqliteConnection(ConnectionString);
    }

    public string ConnectionString { get; }

    public DbProviderFactory Factory { get; }

    public IEntityStoreFactory Stores { get; }

    /// <summary>Creates the Item table with <paramref name="rows"/> rows, ids 1 to <paramref name="rows"/>.</summary>
    public static async Task<BenchmarkDatabase> CreateAsync(string name, int rows)
    {
        var database = new BenchmarkDatabase(name);
        await database._keeper.OpenAsync();
        await database._keeper.ExecuteAsync(
            "DROP TABLE IF EXISTS Item; CREATE TABLE Item (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Quantity INTEGER NOT NULL);");

        await using var transaction = await database._keeper.BeginTransactionAsync();
        for (var i = 1; i <= rows; i++)
        {
            await database._keeper.ExecuteAsync("INSERT INTO Item (Id, Name, Quantity) VALUES (@Id, @Name, @Quantity)",
                new Item { Id = i, Name = $"Item {i}", Quantity = i % 100 }, transaction);
        }
        await transaction.CommitAsync();
        return database;
    }

    public async Task<DbConnection> OpenAsync()
    {
        var connection = Factory.CreateConnection()!;
        await connection.OpenAsync();
        return connection;
    }

    public ValueTask DisposeAsync() => _keeper.DisposeAsync();
}

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Queryeables.Abstraction;
using Queryeables.Dommel;
using Queryeables.Generated;
using Queryeables.Tests.Graphs;

namespace Queryeables.Tests.Contract;

/// <summary>The tables created from the entity classes (<c>CreateTables()</c>), on a real SQLite database.</summary>
public sealed class CreateTablesTests : IAsyncLifetime
{
    private readonly string _connectionString = $"Data Source=tables_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection? _keepAlive;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();
    }

    public async Task DisposeAsync()
    {
        if (_keepAlive is not null)
            await _keepAlive.DisposeAsync();
    }

    [Fact]
    public async Task The_generated_entities_get_their_tables_and_a_graph_is_saved_and_read_in_them()
    {
        await using var app = await QueryeablesHost.CreateAsync(q => q
            .UseSqlite(_connectionString)
            .AddGeneratedEntities()
            .CreateTables());

        var persons = app.GetRequiredService<IGPersonRepository>();
        var ana = new GPerson { Name = "Ana", Contacts = [new GContact { Label = "friend" }] };
        Assert.True((await persons.SaveAsync(ana)).Successful);

        var loaded = await persons.GetAsync(ana.Id);
        Assert.Equal("friend", Assert.Single(loaded!.Contacts).Label);

        await using var command = _keepAlive!.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'GPerson'";    // the class name, not Dommel's plural
        var sql = (string)(await command.ExecuteScalarAsync())!;
        Assert.Contains("INTEGER PRIMARY KEY AUTOINCREMENT", sql);
        Assert.Contains("\"Name\" TEXT NOT NULL", sql);
    }

    [Fact]
    public async Task Creating_the_tables_twice_changes_nothing()
    {
        var factory = new Queryeables.Sqlite.SqliteDbFactory(_connectionString, new ChangeFeedRegistry());

        await DommelSchema.CreateTablesAsync(factory, [typeof(GTag)]);
        await DommelSchema.CreateTablesAsync(factory, [typeof(GTag)]);

        await using var store = DommelEntityStore.Create(factory);
        await store.InsertAsync(new GTag { Name = "red" });
        Assert.Single(await store.GetAllAsync<GTag>());
    }
}

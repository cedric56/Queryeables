using Microsoft.Extensions.DependencyInjection;
using Queryeables;
using Queryeables.Abstraction;
using Queryeables.Dommel;
using Queryeables.Generated;
using Queryeables.Sqlite;
using QueryeablesApp;
using System.Data.Common;

// 1. Register: a provider, then what the attributes of Models.cs describe (the cache, the graph and its repository).
var connectionString = $"Data Source={Path.Combine(AppContext.BaseDirectory, "app.db")}";
var services = new ServiceCollection();
services.UseQueryeables(q => q
    .UseSqlite(connectionString)
    .AddGeneratedEntities());
await using var provider = services.BuildServiceProvider();

await DommelEntityStore.EnsureSchemaAsync(provider.GetRequiredService<DbProviderFactory>(), connectionString, """
    CREATE TABLE IF NOT EXISTS Person  (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS Contact (Id INTEGER PRIMARY KEY AUTOINCREMENT, PersonId INTEGER NOT NULL, Kind TEXT NOT NULL);
    """);

// 2. A cache that follows the table: SQLite reports every change made through Queryeables.
var cache = provider.GetRequiredService<IEntityCache<Person, long>>();
await cache.LoadAsync();

// 3. A person and its contacts, saved in one transaction by the generated repository.
var persons = provider.GetRequiredService<IPersonRepository>();
var ana = new Person { Name = "Ana", Contacts = [new() { Kind = "friend" }, new() { Kind = "colleague" }] };
Check(await persons.SaveAsync(ana));

await WaitForAsync(() => cache.Find(ana.Id) is not null);
Console.WriteLine($"In the cache: {string.Join(", ", cache.Entities.OrderBy(p => p.Id))}");

// 4. Loaded again, with its contacts; then removed, contacts first.
var loaded = new Person { Id = ana.Id, Name = ana.Name };
await persons.LoadContactsAsync(loaded);
Console.WriteLine($"{loaded}: {string.Join(", ", loaded.Contacts!.Select(c => c.Kind))}");

Check(await persons.RemoveAsync(ana));
await WaitForAsync(() => cache.Find(ana.Id) is null);
Console.WriteLine("Removed.");

// A write reports its outcome: no exception for a conflict or a validation error.
static void Check(EntityResult result)
{
    if (!result.Successful)
        throw new InvalidOperationException($"{result.ErrorCode}: {result.Exception}");
}

// The cache updates in the background, just after the commit.
static async Task WaitForAsync(Func<bool> condition)
{
    for (var i = 0; i < 100 && !condition(); i++)
        await Task.Delay(20);
    if (!condition())
        throw new InvalidOperationException("The cache did not follow the table.");
}

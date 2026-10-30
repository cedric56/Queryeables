using Microsoft.Extensions.DependencyInjection;
using Queryeables;
using Queryeables.Abstraction;
using Queryeables.Dommel;
using Queryeables.Generated;
using Queryeables.Sqlite;
using Sample;
using System.Data.Common;

// The whole setup: a provider, then what the attributes describe.
var connectionString = $"Data Source={Path.Combine(Path.GetTempPath(), "queryeables-sample.db")}";
var services = new ServiceCollection();
services.UseQueryeables(q => q
    .UseSqlite(connectionString)
    .AddGeneratedEntities());

await using var provider = services.BuildServiceProvider();

await DommelEntityStore.EnsureSchemaAsync(provider.GetRequiredService<DbProviderFactory>(), connectionString, """
    DROP TABLE IF EXISTS Contact;
    DROP TABLE IF EXISTS Person;
    CREATE TABLE Person  (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL);
    CREATE TABLE Contact (Id INTEGER PRIMARY KEY AUTOINCREMENT, OwnerId INTEGER NOT NULL, PersonId INTEGER NULL, Kind TEXT NOT NULL);
    """);

// The cache follows the database: SQLite reports every change made through Queryeables.
var persons = provider.GetRequiredService<IEntityCache<Person, long>>();
await persons.LoadAsync();

// The generated service saves a person with its contacts, in one transaction.
var service = provider.GetRequiredService<IPersonService>();
var bob = new Person { Name = "Bob" };
Check(await service.SaveAsync(bob));

var ana = new Person { Name = "Ana", Contacts = [new Contact { Kind = "friend", PersonId = bob.Id }, new Contact { Kind = "colleague" }] };
Check(await service.SaveAsync(ana));

await WaitForAsync(() => persons.Entities.Count == 2);
Console.WriteLine($"Cache: {string.Join(", ", persons.Entities.OrderBy(p => p.Id))}");

// Loading sets the navigation properties: the owner, and the other person found in the cache.
var loaded = new Person { Id = ana.Id, Name = ana.Name };
await service.LoadContactsAsync(loaded);
foreach (var contact in loaded.Contacts)
    Console.WriteLine($"{contact.Owner} -> {contact.Kind}: {contact.Person?.Name ?? "(nobody)"}");

// Removing deletes the contacts, then the person.
Check(await service.RemoveAsync(ana));
await WaitForAsync(() => persons.Entities.Count == 1);
Console.WriteLine($"Cache after removal: {string.Join(", ", persons.Entities)}");

static void Check(DbResult result)
{
    if (!result.Successful)
        throw new InvalidOperationException($"{result.ErrorCode}: {result.Exception}");
}

// The cache updates in the background, just after the commit.
static async Task WaitForAsync(Func<bool> condition)
{
    for (var i = 0; i < 50 && !condition(); i++)
        await Task.Delay(20);
    if (!condition())
        throw new InvalidOperationException("The cache did not follow the database.");
}

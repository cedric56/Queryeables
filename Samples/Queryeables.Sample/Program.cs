using Sample;

// The whole setup: a provider, what the attributes describe, the tables. AddEntityServer also makes this application a
// server that publishes replicas to remote clients (last part of the sample).
var file = Path.Combine(Path.GetTempPath(), "queryeables-sample.db");
File.Delete(file);
await using var app = await QueryeablesHost.CreateAsync(q => q
    .UseSqlite($"Data Source={file}")
    .AddGeneratedEntities()
    .CreateTables()
    .AddEntityServer(JsonEntitySerializer.Default, server => server
        .ExposeGenerated(EntityAccessPolicies.AllowAll)));    // in-process only: use Authenticated on a network

// The cache follows the database, and what this application saves is in it as soon as the save returns.
var persons = app.GetRequiredService<IPersonCache>();
await persons.LoadAsync();

// The generated repository saves a person with its contacts, in one transaction.
var service = app.GetRequiredService<IPersonRepository>();
var bob = new Person { Name = "Bob" };
await service.SaveAsync(bob).OrThrow();

var ana = new Person { Name = "Ana", Contacts = [new Contact { Kind = "friend", PersonId = bob.Id }, new Contact { Kind = "colleague" }] };
await service.SaveAsync(ana).OrThrow();
Console.WriteLine($"Cache: {string.Join(", ", persons.Entities.OrderBy(p => p.Id))}");

// Reading sets the navigation properties: the owner, and the other person found in the cache.
var loaded = await service.GetAsync(ana.Id);
foreach (var contact in loaded?.Contacts ?? [])
    Console.WriteLine($"{contact.Owner} -> {contact.Kind}: {contact.Person?.Name ?? "(nobody)"}");

// Removing deletes the contacts, then the person.
await service.RemoveAsync(ana).OrThrow();
Console.WriteLine($"Cache after removal: {string.Join(", ", persons.Entities)}");

// Remote: a client keeps a replica of what the server publishes, the rows its user may read. Here through the dispatcher
// in process; in an application through MagicOnion (q.UseMagicOnion(url)), whose notifications keep the replica up to date.
var dispatcher = app.GetRequiredService<EntityRequestDispatcher>();
await using var client = await QueryeablesHost.CreateAsync(q => q
    .UseStores(_ => RemoteEntityStore.Factory(dispatcher.AsTransport(), JsonEntitySerializer.Default))
    .AddGeneratedEntities());

var replica = client.GetRequiredService<IPersonCache>();
await replica.LoadAsync();
Console.WriteLine($"Replica: {string.Join(", ", replica.Entities.OrderBy(p => p.Id))}");

// A client writes by calling a service of the application, which saves on the server with its own stores (what this line
// does); its replica follows (a notification, here an explicit synchronization).
var carl = new Person { Name = "Carl", Contacts = [new Contact { Kind = "neighbour", PersonId = bob.Id }] };
await service.SaveAsync(carl).OrThrow();
await replica.SyncAsync();
Console.WriteLine($"Replica after the server saved {carl}: {string.Join(", ", replica.Entities.OrderBy(p => p.Id))}");

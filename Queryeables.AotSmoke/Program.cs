using Microsoft.Extensions.DependencyInjection;
using Queryeables;
using Queryeables.Abstraction;
using Queryeables.AotSmoke;
using Queryeables.Generated;
using Queryeables.Remote;

var serializer = new SmokeSerializer();
var database = new MemoryDatabase();

// Server side: the stores, the generated entities, and the remote access.
var services = new ServiceCollection();
services.UseQueryeables(q => q
    .UseStores(_ => new MemoryStore(database, serializer))
    .AddGeneratedEntities()
    .AddEntityServer(serializer, server => server
        .Expose<Person>(EntityAccessPolicies.AllowAll)
        .Expose<Contact>(EntityAccessPolicies.AllowAll)));
await using var provider = services.BuildServiceProvider();

// Local: a generated graph repository, in a transaction.
var persons = provider.GetRequiredService<IPersonRepository>();
var ana = new Person { Name = "Ana", Active = true, Contacts = [new() { Kind = "mail" }, new() { Kind = "phone" }] };
Check(await persons.SaveAsync(ana), "local save");
Expect(ana.Id > 0 && ana.Contacts.All(c => c.Id > 0 && c.PersonId == ana.Id), "keys written back");

var loaded = new Person { Id = ana.Id };
await persons.LoadContactsAsync(loaded);
Expect(loaded.Contacts?.Count == 2 && loaded.Contacts.All(c => c.Owner == loaded), "children loaded, owner set");

// The cache, fed by the stores and updated by its change feed.
var cache = provider.GetRequiredService<IEntityCache<Person, long>>();
await cache.LoadAsync();
Expect(cache.Find(ana.Id)?.Name == "Ana", "cache loaded");

var changed = new TaskCompletionSource();
cache.EntitiesChanged += (_, _) => changed.TrySetResult();
var bob = new Person { Name = "Bob" };
await using (var store = provider.GetRequiredService<IEntityStoreFactory>().Create())
    await store.InsertAsync(bob);
provider.GetRequiredService<IChangeFeed<Person>>().Publish(new EntityChange<Person>(bob.Id, ChangeType.Insert));
await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
Expect(cache.Find(bob.Id)?.Name == "Bob", "cache updated by its monitor");

// Remote: a replica of the in-process server, read by key.
var dispatcher = provider.GetRequiredService<EntityRequestDispatcher>();
await using (var replica = RemoteEntityStore.Factory(dispatcher.AsTransport(), serializer).Create())
{
    Expect((await replica.GetAllAsync<Person>()).Count == 2, "replica read");
    var id = ana.Id;
    Expect((await replica.FirstOrDefaultAsync<Person>(p => p.Id == id))?.Name == "Ana", "replica row by key");
}

// Remote: a client of the in-process server, whose generated cache is a replica of the server's rows.
var clientServices = new ServiceCollection();
clientServices.UseQueryeables(q => q
    .UseStores(_ => RemoteEntityStore.Factory(dispatcher.AsTransport(), serializer))
    .AddGeneratedEntities());
await using var client = clientServices.BuildServiceProvider();

var replicaCache = client.GetRequiredService<IEntityCache<Person, long>>();
await replicaCache.LoadAsync();
Expect(replicaCache.Find(ana.Id)?.Name == "Ana" && replicaCache.Entities.Count == 2, "replica cache loaded");

Console.WriteLine("Queryeables AOT smoke test: OK");
return 0;

static void Check(EntityResult result, string step)
{
    if (!result.Successful)
        throw new InvalidOperationException($"{step}: {result.ErrorCode} {result.Exception}");
}

static void Expect(bool condition, string step)
{
    if (!condition)
        throw new InvalidOperationException($"Failed: {step}.");
}

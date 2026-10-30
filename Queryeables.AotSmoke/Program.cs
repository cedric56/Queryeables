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

// Local: a generated graph service, in a transaction.
var persons = provider.GetRequiredService<IPersonService>();
var ana = new Person { Name = "Ana", Active = true, Contacts = [new() { Kind = "mail" }, new() { Kind = "phone" }] };
Check(await persons.SaveAsync(ana), "local save");
Expect(ana.Id > 0 && ana.Contacts.All(c => c.Id > 0 && c.PersonId == ana.Id), "keys written back");

var loaded = new Person { Id = ana.Id };
await persons.LoadContactsAsync(loaded);
Expect(loaded.Contacts?.Count == 2 && loaded.Contacts.All(c => c.Owner == loaded), "children loaded, owner set");

// The cache, fed by the stores and updated by its monitor.
var cache = provider.GetRequiredService<IEntityCache<Person, long>>();
await cache.LoadAsync();
Expect(cache.Find(ana.Id)?.Name == "Ana", "cache loaded");

var changed = new TaskCompletionSource();
cache.EntitiesChanged += (_, _) => changed.TrySetResult();
var bob = new Person { Name = "Bob" };
await using (var store = provider.GetRequiredService<IEntityStoreFactory>().Create())
    await store.InsertAsync(bob);
provider.GetRequiredService<IDbMonitor<Person>>().Publish(new DbRecord<Person>(bob.Id, ChangeType.Insert));
await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
Expect(cache.Find(bob.Id)?.Name == "Bob", "cache updated by its monitor");

// Remote: a client of the in-process server, with the same generated service.
var dispatcher = provider.GetRequiredService<EntityRequestDispatcher>();
var clientServices = new ServiceCollection();
clientServices.UseQueryeables(q => q
    .UseStores(_ => RemoteEntityStore.Factory(dispatcher.AsRemote(), serializer))
    .AddGeneratedEntities());
await using var client = clientServices.BuildServiceProvider();

var carl = new Person { Name = "Carl", Contacts = [new() { Kind = "fax" }] };
Check(await client.GetRequiredService<IPersonService>().SaveAsync(carl), "remote save");
Expect(carl.Id > 0 && carl.Contacts[0].Id > 0, "remote keys written back");

await using (var remote = client.GetRequiredService<IEntityStoreFactory>().Create())
{
    var found = await remote.SelectAsync<Person>(p => p.Active && (p.Name == "Bob" || p.Name.StartsWith("A")));
    Expect(found.Count == 1 && found[0].Name == "Ana", "nested remote filter");

    var transaction = await remote.RunInTransactionAsync(async s =>
    {
        await s.InsertAsync(new Person { Name = "Dan" });
        await s.InsertAsync(new Person { Name = "Eve" });
    });
    Check(transaction, "remote batch");
    Expect(await remote.CountAsync<Person>(p => p.Name == "Dan" || p.Name == "Eve") == 2, "remote batch applied");
}

Console.WriteLine("Queryeables AOT smoke test: OK");
return 0;

static void Check(DbResult result, string step)
{
    if (!result.Successful)
        throw new InvalidOperationException($"{step}: {result.ErrorCode} {result.Exception}");
}

static void Expect(bool condition, string step)
{
    if (!condition)
        throw new InvalidOperationException($"Failed: {step}.");
}

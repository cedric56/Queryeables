using Microsoft.Extensions.DependencyInjection;
using Queryeables;
using Queryeables.Abstraction;
using Queryeables.Generated;
using Queryeables.MagicOnion.Client;
using QueryeablesApp.Shared;

// The same registration as a local application, but the provider is the server: start QueryeablesApp.Server first.
var url = args.FirstOrDefault() ?? "http://localhost:5070";
var services = new ServiceCollection();
services.UseQueryeables(q => q
    .UseMagicOnion(url)
    .AddGeneratedEntities());
await using var provider = services.BuildServiceProvider();
var stores = provider.GetRequiredService<IEntityStoreFactory>();

await WaitForServerAsync(stores);

// The code of a local application: the repository saves the person and its contacts in one call, the server in one
// transaction.
var persons = provider.GetRequiredService<IPersonRepository>();
var ana = new Person { Name = $"Ana {DateTime.Now:HH:mm:ss}", Contacts = [new() { Kind = "friend" }, new() { Kind = "colleague" }] };
var saved = await persons.SaveAsync(ana);
if (!saved.Successful)
    throw new InvalidOperationException($"{saved.ErrorCode}: {saved.Exception}");

await using (var store = stores.Create())
{
    var everyone = await store.Query<Person>().OrderBy(p => p.Id).ToListAsync();
    Console.WriteLine($"On the server: {string.Join(", ", everyone)}");
}

var loaded = new Person { Id = ana.Id, Name = ana.Name };
await persons.LoadContactsAsync(loaded);
Console.WriteLine($"{loaded}: {string.Join(", ", loaded.Contacts!.Select(c => c.Kind))}");

static async Task WaitForServerAsync(IEntityStoreFactory stores)
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await using var store = stores.Create();
            await store.CountAsync<Person>(_ => true);
            return;
        }
        catch (Exception) when (attempt < 60)
        {
            await Task.Delay(500);       // the server is starting
        }
    }
}

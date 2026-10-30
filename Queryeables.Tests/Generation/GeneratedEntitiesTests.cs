using Microsoft.Extensions.DependencyInjection;
using Queryeables.Abstraction;
using Queryeables.Generated;
using Queryeables.Tests.Graphs;

namespace Queryeables.Tests;

/// <summary>The code written by the source generator, used through dependency injection as an application does.</summary>
public sealed class GeneratedEntitiesTests
{
    private readonly MemoryDatabase _database = new();

    private ServiceProvider Services(bool remote = false)
    {
        var services = new ServiceCollection();
        services.UseQueryeables(q => q
            .UseStores(_ => new EntityStoreFactory(() => new MemoryStore(_database), remote))
            .AddGeneratedEntities());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_lookup_of_another_type_gets_its_own_cache()
    {
        await using var provider = Services();
        await using (var store = new MemoryStore(_database))
            await store.InsertAsync(new GProduct { Name = "Tea" });
        await provider.GetRequiredService<IEntityCache<GProduct, long>>().LoadAsync();

        var orders = provider.GetRequiredService<IGOrderService>();
        var order = new GOrder { Lines = [new GOrderLine { ProductId = 1, Quantity = 2 }] };
        Assert.True((await orders.SaveAsync(order)).Successful);

        var loaded = new GOrder { Id = order.Id };
        await orders.LoadLinesAsync(loaded);

        var line = Assert.Single(loaded.Lines);
        Assert.Equal(order.Id, line.GOrderId);         // foreign key written by convention
        Assert.Same(loaded, line.Order);                // parent navigation
        Assert.Equal("Tea", line.Product?.Name);        // lookup through the product cache
    }

    [Fact]
    public async Task A_nullable_lookup_key_and_a_named_foreign_key_work()
    {
        await using var provider = Services();
        var persons = provider.GetRequiredService<IGPersonService>();
        var bob = new GPerson { Name = "Bob" };
        Assert.True((await persons.SaveAsync(bob)).Successful);
        await provider.GetRequiredService<IEntityCache<GPerson, long>>().LoadAsync();

        var ana = new GPerson { Name = "Ana", Contacts = [new GContact { Label = "friend", PersonId = bob.Id }, new GContact { Label = "none" }] };
        Assert.True((await persons.SaveAsync(ana)).Successful);

        var loaded = new GPerson { Id = ana.Id };
        await persons.LoadContactsAsync(loaded);

        Assert.Equal(2, loaded.Contacts.Count);
        Assert.All(loaded.Contacts, c => Assert.Equal(ana.Id, c.OwnerId));
        Assert.Equal("Bob", loaded.Contacts.Single(c => c.Label == "friend").Person?.Name);
        Assert.Null(loaded.Contacts.Single(c => c.Label == "none").Person);
    }

    [Fact]
    public void The_service_is_also_the_transaction_of_the_type()
    {
        using var provider = Services();
        Assert.Same(provider.GetRequiredService<IGPersonService>(), provider.GetRequiredService<IDbTransaction<GPerson>>());
    }

    [Fact]
    public async Task With_remote_stores_the_server_saves_the_children()
    {
        await using var provider = Services(remote: true);
        var order = new GOrder { Lines = [new GOrderLine { ProductId = 1 }] };

        Assert.True((await provider.GetRequiredService<IGOrderService>().SaveAsync(order)).Successful);

        Assert.Single(_database.All<GOrder>());
        Assert.Empty(_database.All<GOrderLine>());     // the parent only: the server's graph service saves the lines
    }

    [Fact]
    public void Disposing_the_container_synchronously_does_not_fail_on_stores()
    {
        var services = new ServiceCollection();
        services.AddTransient<IEntityStore>(_ => new MemoryStore(_database));
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IEntityStore>();

        provider.Dispose();   // threw "only implements IAsyncDisposable" when stores were not IDisposable
    }

    [Fact]
    public void Queryeables_without_a_store_says_what_to_call()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().UseQueryeables(q => { }));
        Assert.Contains("UseSqlite", error.Message);
    }
}

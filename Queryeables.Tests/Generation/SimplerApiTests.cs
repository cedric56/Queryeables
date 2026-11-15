using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Queryeables.Abstraction;
using Queryeables.Generated;
using Queryeables.Remote;
using Queryeables.Offline;
using Queryeables.Tests.Graphs;

namespace Queryeables.Tests;

/// <summary>The shorter paths of the API: what an application writes in its first hour.</summary>
public sealed class SimplerApiTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();

    private Task<ServiceProvider> AppAsync(Action<QueryeablesBuilder>? more = null)
        => QueryeablesHost.CreateAsync(q =>
        {
            q.UseStores(_ => new EntityStoreFactory(() => new MemoryStore(_database))).AddGeneratedEntities();
            more?.Invoke(q);
        });

    [Fact]
    public async Task What_a_repository_saves_is_in_the_cache_when_the_save_returns()
    {
        await using var app = await AppAsync();
        var cache = app.GetRequiredService<IGPersonCache>();         // generated: no type of key to repeat
        await cache.LoadAsync();

        var ana = new GPerson { Name = "Ana" };
        Assert.True((await app.GetRequiredService<IGPersonRepository>().SaveAsync(ana)).Successful);

        Assert.Equal("Ana", cache.Find(ana.Id)?.Name);              // no notification needed: the write itself

        await using (var store = app.GetRequiredService<IEntityStoreFactory>().Create())
            await store.DeleteAsync(ana);
        Assert.Null(cache.Find(ana.Id));
    }

    [Fact]
    public async Task A_write_in_a_transaction_reaches_the_cache_at_the_commit_only()
    {
        await using var app = await AppAsync();
        var cache = app.GetRequiredService<IGPersonCache>();
        await cache.LoadAsync();
        await using var store = app.GetRequiredService<IEntityStoreFactory>().Create();

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await store.InsertAsync(new GPerson { Name = "Rolled back" });
            await transaction.RollbackAsync();
        }
        Assert.Empty(cache.Entities);

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await store.InsertAsync(new GPerson { Name = "Kept" });
            Assert.Empty(cache.Entities);
            await transaction.CommitAsync();
        }
        Assert.Equal(["Kept"], cache.Entities.Select(p => p.Name));
    }

    [Fact]
    public async Task A_repository_reads_a_parent_with_its_children_in_one_call()
    {
        await using var app = await AppAsync();
        var orders = app.GetRequiredService<IGOrderRepository>();
        var order = new GOrder { Lines = [new GOrderLine { ProductId = 1, Quantity = 2 }, new GOrderLine { ProductId = 2, Quantity = 1 }] };
        Assert.True((await orders.SaveAsync(order)).Successful);

        var loaded = await orders.GetAsync(order.Id);

        Assert.Equal(2, loaded?.Lines.Count);
        Assert.Null(await orders.GetAsync(order.Id + 100));
    }

    [Fact]
    public async Task A_cache_marked_preload_is_loaded_at_startup()
    {
        await using (var store = new MemoryStore(_database))
            await store.InsertAsync(new GPerson { Name = "Ana" });

        await using var app = await AppAsync(q => q.Preload<GPerson, long>());

        Assert.Equal(CacheFreshness.Current, ((EntityCache<GPerson, long>)app.GetRequiredService<IEntityCache<GPerson, long>>()).Freshness);
        Assert.Single(app.GetRequiredService<IGPersonCache>().Entities);
    }

    [Fact]
    public async Task ThrowOnFailure_turns_a_failed_result_into_an_exception()
    {
        await using var app = await AppAsync(q => q.ThrowOnFailure());
        var persons = app.GetRequiredService<IGPersonRepository>();

        var missing = new GPerson { Id = 42, Name = "Ghost" };              // not stored: the update finds nothing
        var thrown = await Assert.ThrowsAsync<EntityStoreException>(() => persons.SaveAsync(missing));

        Assert.Equal(EntityErrorCode.NotFound, thrown.ErrorCode);
    }

    [Fact]
    public async Task A_store_saves_an_entity_without_being_told_its_key()
    {
        await using var store = new MemoryStore(_database);
        var product = new GProduct { Name = "Tea" };

        await store.SaveAsync(product);                                     // new: inserted
        product.Name = "Green tea";
        await store.SaveAsync(product);                                     // has a key: updated

        Assert.Equal("Green tea", Assert.Single(_database.All<GProduct>()).Name);
    }

    [Fact]
    public async Task The_generator_declares_every_entity_it_knows_with_its_key()
    {
        await using var app = await AppAsync();

        var types = app.GetServices<IEntityType>().Select(t => t.Type).ToHashSet();

        Assert.Contains(typeof(GPerson), types);        // [EntityCache]
        Assert.Contains(typeof(GContact), types);       // a child
        Assert.Contains(typeof(GProduct), types);       // a lookup target
        Assert.All(app.GetServices<IEntityType>(), t => Assert.Equal(typeof(long), t.KeyType));
    }

    // ---------- A server that exposes every generated entity, a client that copies them offline ----------

    [Fact]
    public async Task ExposeGenerated_exposes_every_entity_and_keeps_the_ones_exposed_one_by_one()
    {
        var services = new ServiceCollection();
        services.UseQueryeables(q => q
            .UseStores(_ => new EntityStoreFactory(() => new MemoryStore(_database)))
            .AddGeneratedEntities()
            .AddEntityServer(_serializer, server => server
                .AllowWrites()
                .Expose<GProduct>(EntityAccessPolicies.ReadOnly)
                .ExposeGenerated(EntityAccessPolicies.Authenticated)));
        await using var provider = services.BuildServiceProvider();
        var user = new EntityRequestContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ana")], "test")));
        await using var store = new WritableRemoteEntityStore(provider.GetRequiredService<EntityRequestDispatcher>().AsTransport(user), _serializer);

        await store.InsertAsync(new GPerson { Name = "Ana" });              // exposed by ExposeGenerated
        await store.InsertAsync(new GTag { Name = "red" });
        var refused = await Assert.ThrowsAsync<EntityStoreException>(() => store.InsertAsync(new GProduct { Name = "Tea" }));

        Assert.Equal(EntityErrorCode.Forbidden, refused.ErrorCode);         // its own policy: read only
        Assert.Single(_database.All<GPerson>());
    }

    [Fact]
    public void AllGeneratedEntities_makes_every_generated_entity_available_offline_with_its_key()
    {
        var services = new ServiceCollection();
        services.UseQueryeables(q => q
            .UseStores(_ => new EntityStoreFactory(() => new MemoryStore(_database)))
            .AddGeneratedEntities());
        using var provider = services.BuildServiceProvider();

        var mode = new OfflineModeOptions()
            .AllGeneratedEntities()
            .Entity<GPerson>(p => p.Name != "")                             // a filter on one of them, no key to repeat
            .Create(new EntityRequestDispatcher(new EntityHandlerRegistry(), _serializer, new EntityStoreFactory(() => new MemoryStore(_database))).AsTransport(),
                _serializer, services: provider);

        Assert.True(mode.Knows(typeof(GPerson)));
        Assert.True(mode.Knows(typeof(GContact)));
        Assert.True(mode.Knows(typeof(GProduct)));
    }

    [Fact]
    public void An_entity_asked_offline_without_its_key_and_unknown_to_the_generator_is_refused_with_a_message()
    {
        var options = new OfflineModeOptions().Entity<TestClient>();

        var refused = Assert.Throws<InvalidOperationException>(() => options.Create(
            new EntityRequestDispatcher(new EntityHandlerRegistry(), _serializer, new EntityStoreFactory(() => new MemoryStore(_database))).AsTransport(),
            _serializer));

        Assert.Contains("TestClient", refused.Message);
    }

    // ---------- JSON: no attribute on the classes ----------

    [Fact]
    public void The_JSON_serializer_cuts_the_navigation_back_to_the_parent()
    {
        var person = new GPerson { Id = 1, Name = "Ana" };
        person.Contacts.Add(new GContact { Id = 2, OwnerId = 1, Label = "friend", Owner = person });

        var serializer = JsonEntitySerializer.Default;
        var back = serializer.Deserialize<GPerson>(serializer.Serialize(person))!;

        Assert.Equal("friend", Assert.Single(back.Contacts).Label);
        Assert.Null(back.Contacts[0].Owner);                                // a cycle, written once
    }
}

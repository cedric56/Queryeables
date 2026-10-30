using Queryeables.Abstraction;
using Queryeables.Remote;

namespace Queryeables.Tests.Unit;

/// <summary>Parents with children in a remote transaction: the server runs their graph in the batch's transaction.</summary>
public class RemoteGraphBatchTests
{
    private readonly MemoryDatabase _database = new();

    private IEntityStoreFactory Client()
    {
        var serverStores = new EntityStoreFactory(() => new MemoryStore(_database));
        var graph = EntityGraph.For<AuditPerson, long>(p => p.Id)
            .HasMany(p => p.Contacts, foreignKey: c => c.PersonId, key: c => c.Id);
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<AuditPerson>(serverStores, EntityAccessPolicies.AllowAll,
            aggregate: new EntityGraphService<AuditPerson, long>(serverStores, graph)));
        var dispatcher = new EntityRequestDispatcher(registry, new JsonTestSerializer(), serverStores);
        return RemoteEntityStore.Factory(dispatcher.AsRemote(), new JsonTestSerializer());
    }

    [Fact]
    public async Task Several_parents_with_children_are_saved_in_one_batch()
    {
        var ana = new AuditPerson { Name = "Ana", Contacts = [new() { Label = "mail" }] };
        var bob = new AuditPerson { Name = "Bob", Contacts = [new() { Label = "fax" }, new() { Label = "phone" }] };

        await using var store = Client().Create();
        var result = await store.InsertManyAsync([ana, bob]);

        Assert.True(result.Successful);
        Assert.Equal(2, _database.All<AuditPerson>().Count);
        Assert.Equal(3, _database.All<AuditContact>().Count);
        Assert.True(ana.Contacts[0].Id > 0);
        Assert.Equal(bob.Id, bob.Contacts[1].PersonId);
    }

    [Fact]
    public async Task A_parent_removed_in_a_batch_takes_its_children()
    {
        var ana = new AuditPerson { Name = "Ana", Contacts = [new() { Label = "mail" }] };
        await using var store = Client().Create();
        (await store.InsertManyAsync([ana])).ThrowIfFailed();

        var result = await store.DeleteManyAsync([ana]);

        Assert.True(result.Successful);
        Assert.Empty(_database.All<AuditPerson>());
        Assert.Empty(_database.All<AuditContact>());
    }
}

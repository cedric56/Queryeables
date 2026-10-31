using Queryeables.Abstraction;

namespace Queryeables.Tests;

public sealed class AggregatePerson
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<AggregateContact> Contacts { get; set; } = [];
}

public sealed class AggregateContact
{
    public long Id { get; set; }
    public long PersonId { get; set; }
    public string Label { get; set; } = string.Empty;
    public AggregatePerson? Owner { get; set; }
}

public class AggregateTests
{
    private readonly MemoryDatabase _database = new();
    private readonly List<AggregatePerson> _loadedFor = [];

    private EntityGraph<AggregatePerson, long> Persons() => EntityGraph.For<AggregatePerson, long>(p => p.Id)
        .HasMany(p => p.Contacts, foreignKey: c => c.PersonId, key: c => c.Id,
            onLoaded: (person, contacts) =>
            {
                _loadedFor.Add(person);
                foreach (var contact in contacts)
                    contact.Owner = person;
            });

    private GraphRepository<AggregatePerson, long> Service(bool childrenSavedByServer = false)
        => new(() => new MemoryStore(_database), Persons(), childrenSavedByServer);

    private static AggregatePerson Ana() => new()
    {
        Name = "Ana",
        Contacts = [new() { Label = "mail" }, new() { Label = "phone" }]
    };

    [Fact]
    public async Task Saving_a_new_parent_inserts_it_with_its_children()
    {
        var ana = Ana();

        var result = await Service().SaveAsync(ana);

        Assert.True(result.Successful);
        Assert.True(ana.Id > 0);
        Assert.All(_database.All<AggregateContact>(), c => Assert.Equal(ana.Id, c.PersonId));
        Assert.Equal(new[] { "mail", "phone" }, _database.All<AggregateContact>().Select(c => c.Label));
        Assert.All(ana.Contacts, c => Assert.True(c.Id > 0));
    }

    [Fact]
    public async Task Saving_an_existing_parent_aligns_its_children()
    {
        var service = Service();
        var ana = Ana();
        await service.SaveAsync(ana);

        ana.Name = "Ana B";
        ana.Contacts.RemoveAt(0);                       // "mail" removed
        ana.Contacts[0].Label = "mobile";               // "phone" renamed
        ana.Contacts.Add(new() { Label = "fax" });      // new child
        var result = await service.SaveAsync(ana);

        Assert.True(result.Successful);
        Assert.Equal("Ana B", Assert.Single(_database.All<AggregatePerson>()).Name);
        Assert.Equal(new[] { "mobile", "fax" }, _database.All<AggregateContact>().Select(c => c.Label).OrderByDescending(l => l));
    }

    [Fact]
    public async Task Removing_a_parent_deletes_its_children_first()
    {
        var service = Service();
        var ana = Ana();
        await service.SaveAsync(ana);
        _database.Log.Clear();

        var result = await service.RemoveAsync(new AggregatePerson { Id = ana.Id });

        Assert.True(result.Successful);
        Assert.Empty(_database.All<AggregatePerson>());
        Assert.Empty(_database.All<AggregateContact>());
        Assert.Equal($"DELETE {nameof(AggregatePerson)} {ana.Id}", _database.Log[^2]);   // parent last, then COMMIT
    }

    [Fact]
    public async Task Loading_reads_the_children_and_calls_onLoaded()
    {
        var service = Service();
        var ana = Ana();
        await service.SaveAsync(ana);

        var copy = new AggregatePerson { Id = ana.Id, Name = "Ana" };
        await service.LoadChildrenAsync(copy);

        Assert.Equal(2, copy.Contacts.Count);
        Assert.All(copy.Contacts, c => Assert.Same(copy, c.Owner));
        Assert.Contains(copy, _loadedFor);
    }

    [Fact]
    public async Task A_new_parent_loads_empty_children_without_querying()
    {
        var person = new AggregatePerson { Contacts = [new() { Label = "draft" }] };

        await Service().LoadChildrenAsync(person);

        Assert.Empty(person.Contacts);
        Assert.Empty(_database.Log);
    }

    [Fact]
    public async Task Updating_a_vanished_parent_fails_and_writes_nothing()
    {
        var ghost = new AggregatePerson { Id = 42, Name = "?", Contacts = [new() { Label = "x" }] };

        var result = await Service().SaveAsync(ghost);

        Assert.Equal(EntityErrorCode.NotFound, result.ErrorCode);
        Assert.Empty(_database.All<AggregateContact>());
        Assert.DoesNotContain("COMMIT", _database.Log);
    }

    [Fact]
    public async Task When_the_server_saves_the_children_only_the_parent_is_sent()
    {
        var ana = Ana();

        var result = await Service(childrenSavedByServer: true).SaveAsync(ana);

        Assert.True(result.Successful);
        Assert.Equal(new[] { $"INSERT {nameof(AggregatePerson)} 1" }, _database.Log);
    }

    [Fact]
    public async Task SaveAsync_inserts_new_entities_and_updates_the_others()
    {
        await using var store = new MemoryStore(_database);
        var ana = new AggregatePerson { Name = "Ana" };

        await store.SaveAsync(ana, p => p.Id);
        ana.Name = "Ana B";
        await store.SaveAsync(ana, p => p.Id);

        Assert.Equal(new[] { $"INSERT {nameof(AggregatePerson)} 1", $"UPDATE {nameof(AggregatePerson)} 1" }, _database.Log);
        var error = await Assert.ThrowsAsync<EntityStoreException>(() => store.SaveAsync(new AggregatePerson { Id = 9 }, p => p.Id));
        Assert.Equal(EntityErrorCode.NotFound, error.ErrorCode);
    }

    [Fact]
    public void Relations_must_target_writable_properties()
    {
        var aggregate = EntityGraph.For<AggregatePerson, long>(p => p.Id);

        Assert.Throws<ArgumentException>(() =>
            aggregate.HasMany(p => p.Contacts.Where(c => c.Id > 0), c => c.PersonId, c => c.Id));
        Assert.Throws<ArgumentException>(() =>
            aggregate.HasMany(p => p.Contacts, c => c.PersonId + 1, c => c.Id));
    }
}

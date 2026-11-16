using Queryeables.Abstraction;

namespace Queryeables.Tests.Unit;

/// <summary>The children of a graph: saved, removed, loaded and validated with their parent.</summary>
public class GraphChildrenTests
{
    private readonly MemoryDatabase _database = new();

    private static EntityGraph<Member, long> Graph() => EntityGraph.For<Member, long>(p => p.Id)
        .HasMany(p => p.Contacts, foreignKey: c => c.PersonId, key: c => c.Id,
            onLoaded: (person, contacts) => { foreach (var c in contacts) c.Owner = person; })
        .HasMany(p => p.Notes, foreignKey: n => n.PersonId, key: n => n.Id);

    private GraphRepository<Member, long> LocalService() => new(() => new MemoryStore(_database), Graph());

    private static Member Ana() => new()
    {
        Name = "Ana",
        Contacts = [new() { Label = "mail" }, new() { Label = "phone" }],
        Notes = [new() { Text = "vip" }],
    };

    [Fact]
    public async Task A_null_collection_leaves_the_stored_children_alone()
    {
        var ana = Ana();
        (await LocalService().SaveAsync(ana)).ThrowIfFailed();

        // As read from a cache: the children were not loaded.
        var fromCache = new Member { Id = ana.Id, Name = "Ana B" };
        var result = await LocalService().SaveAsync(fromCache);

        Assert.True(result.Successful);
        Assert.Equal(2, _database.All<MemberContact>().Count);
        Assert.Single(_database.All<MemberNote>());
    }

    [Fact]
    public async Task An_empty_collection_deletes_the_stored_children()
    {
        var ana = Ana();
        (await LocalService().SaveAsync(ana)).ThrowIfFailed();

        ana.Contacts = [];
        (await LocalService().SaveAsync(ana)).ThrowIfFailed();

        Assert.Empty(_database.All<MemberContact>());
        Assert.Single(_database.All<MemberNote>());
    }

    [Fact]
    public async Task A_child_of_another_parent_cannot_be_taken_over()
    {
        var ana = Ana();
        var bob = new Member { Name = "Bob", Contacts = [new() { Label = "bob's" }] };
        (await LocalService().SaveAsync(ana)).ThrowIfFailed();
        (await LocalService().SaveAsync(bob)).ThrowIfFailed();

        // Ana's list now claims Bob's contact by its key.
        ana.Contacts!.Add(new MemberContact { Id = bob.Contacts![0].Id, Label = "stolen" });
        var result = await LocalService().SaveAsync(ana);

        Assert.False(result.Successful);
        Assert.Equal(EntityErrorCode.Conflict, result.ErrorCode);
        var bobsContact = Assert.Single(_database.All<MemberContact>(), c => c.Id == bob.Contacts[0].Id);
        Assert.Equal(bob.Id, bobsContact.PersonId);
        Assert.Equal("bob's", bobsContact.Label);
    }

    [Fact]
    public async Task Loading_one_collection_leaves_the_others_untouched()
    {
        var ana = Ana();
        (await LocalService().SaveAsync(ana)).ThrowIfFailed();

        var loaded = new Member { Id = ana.Id };
        await LocalService().LoadChildrenAsync(loaded, nameof(Member.Contacts));

        Assert.Equal(2, loaded.Contacts!.Count);
        Assert.All(loaded.Contacts, c => Assert.Same(loaded, c.Owner));
        Assert.Null(loaded.Notes);
        await Assert.ThrowsAsync<ArgumentException>(() => LocalService().LoadChildrenAsync(loaded, "Unknown"));
    }

    [Fact]
    public async Task SyncChildren_reports_a_child_that_vanished()
    {
        await using var store = new MemoryStore(_database);
        var vanished = new MemberContact { Id = 42, PersonId = 1, Label = "gone" };

        var result = await store.RunInTransactionAsync(s =>
            s.SyncChildrenAsync(Array.Empty<MemberContact>(), [vanished], c => c.Id == 0, (a, b) => a.Id == b.Id));

        Assert.False(result.Successful);
        Assert.Equal(EntityErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public void The_children_of_a_graph_are_validated_with_the_parent()
    {
        var folder = new ValidatedFolder { Name = "ok", Notes = [new ValidatedNote { Text = "fine" }, new ValidatedNote { Text = "far too long" }, new ValidatedNote()] };

        var errors = EntityValidator.ValidateGraph(folder);

        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.StartsWith("Notes[", e.ErrorMessage));
        Assert.Contains(errors, e => e.ErrorMessage!.StartsWith("Notes[1]:", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.ErrorMessage!.StartsWith("Notes[2]:", StringComparison.Ordinal));
    }
}

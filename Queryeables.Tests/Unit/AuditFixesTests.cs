using Queryeables.Abstraction;
using Queryeables.Remote;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Queryeables.Tests.Unit;

public sealed class AuditPerson
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Not carried by the serializer: the client's value must survive a remote save.</summary>
    [JsonIgnore]
    public string LocalTag { get; set; } = string.Empty;

    public List<AuditContact>? Contacts { get; set; }
    public List<AuditNote>? Notes { get; set; }
}

public sealed class AuditContact
{
    public long Id { get; set; }
    public long PersonId { get; set; }
    public string Label { get; set; } = string.Empty;

    [JsonIgnore]
    public AuditPerson? Owner { get; set; }
}

public sealed class AuditNote
{
    public long Id { get; set; }
    public long PersonId { get; set; }
    public string Text { get; set; } = string.Empty;
}

[Table("Audit_Rows")]
public sealed class AuditTableEntity
{
    public long Id { get; set; }
}

public record AuditTag(string Name);

public class AuditFixesTests
{
    private readonly MemoryDatabase _database = new();

    private static EntityGraph<AuditPerson, long> Graph() => EntityGraph.For<AuditPerson, long>(p => p.Id)
        .HasMany(p => p.Contacts, foreignKey: c => c.PersonId, key: c => c.Id,
            onLoaded: (person, contacts) => { foreach (var c in contacts) c.Owner = person; })
        .HasMany(p => p.Notes, foreignKey: n => n.PersonId, key: n => n.Id);

    private EntityGraphService<AuditPerson, long> LocalService() => new(() => new MemoryStore(_database), Graph());

    private static AuditPerson Ana() => new()
    {
        Name = "Ana",
        Contacts = [new() { Label = "mail" }, new() { Label = "phone" }],
        Notes = [new() { Text = "vip" }],
    };

    // ---------- Entity graphs ----------

    [Fact]
    public async Task A_null_collection_leaves_the_stored_children_alone()
    {
        var ana = Ana();
        (await LocalService().SaveAsync(ana)).ThrowIfFailed();

        // As read from a cache: the children were not loaded.
        var fromCache = new AuditPerson { Id = ana.Id, Name = "Ana B" };
        var result = await LocalService().SaveAsync(fromCache);

        Assert.True(result.Successful);
        Assert.Equal(2, _database.All<AuditContact>().Count);
        Assert.Single(_database.All<AuditNote>());
    }

    [Fact]
    public async Task An_empty_collection_deletes_the_stored_children()
    {
        var ana = Ana();
        (await LocalService().SaveAsync(ana)).ThrowIfFailed();

        ana.Contacts = [];
        (await LocalService().SaveAsync(ana)).ThrowIfFailed();

        Assert.Empty(_database.All<AuditContact>());
        Assert.Single(_database.All<AuditNote>());
    }

    [Fact]
    public async Task A_child_of_another_parent_cannot_be_taken_over()
    {
        var ana = Ana();
        var bob = new AuditPerson { Name = "Bob", Contacts = [new() { Label = "bob's" }] };
        (await LocalService().SaveAsync(ana)).ThrowIfFailed();
        (await LocalService().SaveAsync(bob)).ThrowIfFailed();

        // Ana's list now claims Bob's contact by its key.
        ana.Contacts!.Add(new AuditContact { Id = bob.Contacts![0].Id, Label = "stolen" });
        var result = await LocalService().SaveAsync(ana);

        Assert.False(result.Successful);
        Assert.Equal(EntityErrorCode.Conflict, result.ErrorCode);
        var bobsContact = Assert.Single(_database.All<AuditContact>(), c => c.Id == bob.Contacts[0].Id);
        Assert.Equal(bob.Id, bobsContact.PersonId);
        Assert.Equal("bob's", bobsContact.Label);
    }

    [Fact]
    public async Task Loading_one_collection_leaves_the_others_untouched()
    {
        var ana = Ana();
        (await LocalService().SaveAsync(ana)).ThrowIfFailed();

        var loaded = new AuditPerson { Id = ana.Id };
        await LocalService().LoadChildrenAsync(loaded, nameof(AuditPerson.Contacts));

        Assert.Equal(2, loaded.Contacts!.Count);
        Assert.All(loaded.Contacts, c => Assert.Same(loaded, c.Owner));
        Assert.Null(loaded.Notes);
        await Assert.ThrowsAsync<ArgumentException>(() => LocalService().LoadChildrenAsync(loaded, "Unknown"));
    }

    [Fact]
    public async Task SyncChildren_reports_a_child_that_vanished()
    {
        await using var store = new MemoryStore(_database);
        var vanished = new AuditContact { Id = 42, PersonId = 1, Label = "gone" };

        var result = await store.RunInTransactionAsync(s =>
            s.SyncChildrenAsync(Array.Empty<AuditContact>(), [vanished], c => c.Id == 0, (a, b) => a.Id == b.Id));

        Assert.False(result.Successful);
        Assert.Equal(EntityErrorCode.NotFound, result.ErrorCode);
    }

    // ---------- Remote store ----------

    [Fact]
    public async Task A_remote_save_keeps_the_callers_objects_and_fills_their_keys()
    {
        var serverStores = new EntityStoreFactory(() => new MemoryStore(_database));
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<AuditPerson>(serverStores, EntityAccessPolicies.AllowAll,
            aggregate: new EntityGraphService<AuditPerson, long>(serverStores, Graph())));
        var dispatcher = new EntityRequestDispatcher(registry, new JsonTestSerializer());
        var client = new EntityGraphService<AuditPerson, long>(
            RemoteEntityStore.Factory(dispatcher.AsRemote(), new JsonTestSerializer()), Graph());

        var ana = Ana();
        ana.LocalTag = "selected";
        var mail = ana.Contacts![0];
        mail.Owner = ana;

        (await client.SaveAsync(ana)).ThrowIfFailed();

        Assert.True(ana.Id > 0);
        Assert.Equal("selected", ana.LocalTag);           // not serialized: not reset
        Assert.Same(mail, ana.Contacts[0]);               // same instances...
        Assert.True(mail.Id > 0);                         // ...with the keys generated by the server
        Assert.Equal(ana.Id, mail.PersonId);
        Assert.Same(ana, mail.Owner);                     // navigation kept
        Assert.True(ana.Notes![0].Id > 0);
    }

    // ---------- Cache and monitors ----------

    [Fact]
    public async Task A_key_that_cannot_be_parsed_synchronizes_the_cache()
    {
        var rows = new List<TestClient> { new() { Id = 1, Name = "Ana" } };
        var monitor = new DbMonitor<TestClient>();
        using var cache = new EntityCache<TestClient, long>(() => new InMemoryEntityStore(rows), c => c.Id, monitor,
            DbRecordKey.Parse<long>);
        await cache.LoadAsync();
        Exception? failure = null;
        cache.RefreshFailed += (_, ex) => failure = ex;

        rows.Add(new TestClient { Id = 2, Name = "Bob" });
        var changed = new TaskCompletionSource();
        cache.EntitiesChanged += (_, _) => changed.TrySetResult();
        monitor.Publish(new DbRecord<TestClient>("not-a-number", ChangeType.Insert));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(failure);
        Assert.Equal("Bob", cache.Find(2)?.Name);
    }

    [Fact]
    public void A_monitor_follows_the_table_attribute()
    {
        Assert.Equal("Audit_Rows", new DbMonitor<AuditTableEntity>().Table);
        Assert.Equal(nameof(AuditPerson), new DbMonitor<AuditPerson>().Table);
        Assert.Equal("Other", new DbMonitor<AuditTableEntity>("Other").Table);
    }

    // ---------- Filters ----------

    [Fact]
    public void The_server_rejects_an_oversized_In_list()
    {
        var values = Enumerable.Range(0, Filters.MaxValues + 1).Select(i => (object?)(long)i).ToArray();
        var condition = new FilterCondition(nameof(TestClient.Id), FilterOperator.In, values);

        Assert.Throws<ArgumentException>(() => Filters.ToPredicate<TestClient>([condition], any: false));
        Assert.NotNull(Filters.ToPredicate<TestClient>([new FilterCondition(nameof(TestClient.Id), FilterOperator.In, values[..Filters.MaxValues])], any: false));
    }

    // ---------- Collections ----------

    [Fact]
    public void Update_pairs_items_one_to_one()
    {
        var contacts = new List<AuditContact> { new() { PersonId = 1, Label = "kept" }, new() { PersonId = 3, Label = "removed" } };

        contacts.Update(new long[] { 1, 2 }, (id, c) => c.PersonId == id, id => new AuditContact { PersonId = id, Label = "new" });

        Assert.Equal(new[] { "kept", "new" }, contacts.Select(c => c.Label));
    }

    [Fact]
    public void Update_works_with_value_types_and_records()
    {
        var numbers = new List<int> { 0, 5 };
        numbers.Update(new[] { 0, 7 }, (s, t) => s == t, s => s);
        Assert.Equal(new[] { 0, 7 }, numbers);

        // Two equal records stay two items.
        var tags = new List<AuditTag> { new("a"), new("a") };
        tags.Update(new[] { "a", "a" }, (s, t) => t.Name == s, s => new AuditTag(s));
        Assert.Equal(2, tags.Count);
    }

    [Fact]
    public void Update_keeps_a_created_item_that_does_not_match_its_source()
    {
        var target = new List<string>();
        target.Update(new[] { 1 }, (s, t) => t == s.ToString(), s => $"#{s}");
        Assert.Equal(new[] { "#1" }, target);
    }
}

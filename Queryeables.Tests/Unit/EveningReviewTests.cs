using System.Reflection;
using Queryeables.Abstraction;
using Queryeables.Remote;

namespace Queryeables.Tests.Unit;

/// <summary>The fixes of the review of November 15 (evening), each against the way it showed.</summary>
public class EveningReviewTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();

    private IEntityStoreFactory Stores => new EntityStoreFactory(() => new MemoryStore(_database));

    // ---------- 2. The cache keeps a copy of a row saved, not the application's instance ----------

    [Fact]
    public async Task A_saved_entity_changed_afterwards_without_a_save_is_not_changed_in_the_cache()
    {
        var writes = new LocalWrites();
        using var cache = new EntityCache<TestClient, long>(Stores, c => c.Id, localWrites: writes);
        await using var store = new LocalWritesDecorator(new MemoryStore(_database), writes);

        var ana = new TestClient { Name = "Ana" };
        await store.InsertAsync(ana);
        ana.Name = "Ana (not saved)";                                           // a form cancelled

        var cached = cache.Find(ana.Id) ?? throw new InvalidOperationException("Not in the cache.");
        Assert.False(ReferenceEquals(ana, cached));
        Assert.Equal("Ana", cached.Name);
    }

    [Fact]
    public async Task A_saved_entity_is_cached_without_its_loaded_children()
    {
        var writes = new LocalWrites();
        using var cache = new EntityCache<SOrder, long>(Stores, o => o.Id, localWrites: writes);
        await using var store = new LocalWritesDecorator(new MemoryStore(_database), writes);

        var order = new SOrder { Customer = "Ana", SOrderLines = [new SOrderLine { Product = "Tea" }] };
        await store.InsertAsync(order);

        var cached = cache.Find(order.Id) ?? throw new InvalidOperationException("Not in the cache.");
        Assert.Equal("Ana", cached.Customer);
        Assert.Null(cached.SOrderLines);                                        // as a row read from the database
    }

    // ---------- 4. A type the serializer would leak is reported at startup ----------

    /// <summary>A serializer that honors [IgnoreMember] only, as MessagePack does.</summary>
    private sealed class MessagePackLike(JsonTestSerializer json) : IEntitySerializer
    {
        public byte[] Serialize<T>(T value) => json.Serialize(value);
        public T? Deserialize<T>(byte[] data) => json.Deserialize<T>(data);
        public bool Omits(PropertyInfo property) => RemoteMembers.Has(property, "IgnoreMemberAttribute");
    }

    [Fact]
    public void The_configuration_check_names_a_hidden_property_the_serializer_would_send()
    {
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<SAccount>(Stores, EntityAccessPolicies.AllowAll));
        registry.Register(new EntityHandler<TestClient>(Stores, EntityAccessPolicies.AllowAll));

        new EntityRequestDispatcher(registry, _serializer).CheckConfiguration();     // JSON honors [JsonIgnore]
        var error = Assert.Throws<InvalidOperationException>(
            () => new EntityRequestDispatcher(registry, new MessagePackLike(_serializer)).CheckConfiguration());
        Assert.Contains("SAccount.PasswordHash", error.Message);
    }
}

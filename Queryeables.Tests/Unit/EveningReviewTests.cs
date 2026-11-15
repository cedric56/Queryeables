using System.Reflection;
using Queryeables.Offline;
using Queryeables.Abstraction;
using Queryeables.Remote;

namespace Queryeables.Tests.Unit;

/// <summary>The fixes of the review of November 15 (evening), each against the way it showed.</summary>
public class EveningReviewTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();

    private IEntityStoreFactory Stores => new EntityStoreFactory(() => new MemoryStore(_database));

    // ---------- 1. The writes of many rows reach their author's caches ----------

    /// <summary>The notification sessions the client sends with its writes.</summary>
    private sealed class Sessions(IEntityTransport inner) : IEntityTransport
    {
        public List<(string Write, string? Session)> Sent { get; } = [];
        public Task<TransportResult> GetAllAsync(string type, CancellationToken ct = default) => inner.GetAllAsync(type, ct);
        public Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, bool any, CancellationToken ct = default) => inner.SelectAsync(type, conditions, any, ct);
        public Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize, CancellationToken ct = default)
            => inner.SelectPagedAsync(type, conditions, any, pageNumber, pageSize, ct);
        public Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default) => inner.GetChangesAsync(type, sinceUtc, ct);
        public Task<TransportResult> InsertAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
        {
            Sent.Add(("insert", connectionId));
            return inner.InsertAsync(type, entity, connectionId, ct);
        }
        public Task<TransportResult> UpdateAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default) => inner.UpdateAsync(type, entity, connectionId, ct);
        public Task<TransportResult> DeleteAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
        {
            Sent.Add(("delete", connectionId));
            return inner.DeleteAsync(type, entity, connectionId, ct);
        }
        public Task<TransportResult> ExecuteBatchAsync(BatchOperation[] operations, CancellationToken ct = default) => inner.ExecuteBatchAsync(operations, ct);
        public Task<TransportResult> ExecuteAsync(string type, RemoteRequest request, CancellationToken ct = default)
        {
            Sent.Add((request.Kind.ToString(), request.ConnectionId));
            return inner.ExecuteAsync(type, request, ct);
        }
    }

    [Fact]
    public async Task A_write_of_many_rows_is_sent_without_the_session_so_that_its_author_is_notified()
    {
        var registry = new EntityHandlerRegistry();
        registry.Register(new WritableEntityHandler<TestClient>(Stores, EntityAccessPolicies.AllowAll));
        var transport = new Sessions(new EntityRequestDispatcher(registry, _serializer, Stores).AsTransport());
        var session = new ChangeSession();
        session.SetSession(typeof(TestClient), ("me", () => Task.CompletedTask));
        await using var store = new WritableRemoteEntityStore(transport, _serializer, session);

        var ana = new TestClient { Name = "Ana" };
        await store.InsertAsync(ana);
        await store.InsertAsync(new TestClient { Name = "Bob" });
        await store.UpdateWhereAsync<TestClient>(c => c.Name == "Bob", set => set.Set(c => c.Active, true));
        await store.DeleteWhereAsync<TestClient>(c => c.Active);
        await store.DeleteAsync(ana);

        Assert.Equal(
            new (string, string?)[] { ("insert", "me"), ("insert", "me"), ("UpdateWhere", null), ("DeleteWhere", null), ("delete", "me") },
            transport.Sent);                                                    // a single write: its caches apply it at once
        Assert.Empty(_database.All<TestClient>());
    }

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
        registry.Register(new WritableEntityHandler<SAccount>(Stores, EntityAccessPolicies.AllowAll));
        registry.Register(new WritableEntityHandler<TestClient>(Stores, EntityAccessPolicies.AllowAll));

        new EntityRequestDispatcher(registry, _serializer, Stores).CheckConfiguration();     // JSON honors [JsonIgnore]
        var error = Assert.Throws<InvalidOperationException>(
            () => new EntityRequestDispatcher(registry, new MessagePackLike(_serializer), Stores).CheckConfiguration());
        Assert.Contains("SAccount.PasswordHash", error.Message);
    }
}

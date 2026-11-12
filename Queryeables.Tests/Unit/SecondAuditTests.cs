using System.Reflection;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Queryeables.Abstraction;
using Queryeables.MagicOnion;
using Queryeables.Remote;

namespace Queryeables.Tests.Unit;

/// <summary>An account whose hash never leaves the server, and whose role the server decides.</summary>
public sealed class SAccount
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    [JsonIgnore] public string PasswordHash { get; set; } = string.Empty;
    [ServerOwned] public string? Role { get; set; }
}

/// <summary>An order and its lines: the price of a line is the server's.</summary>
public sealed class SOrder
{
    public long Id { get; set; }
    public string Customer { get; set; } = string.Empty;
    public List<SOrderLine>? SOrderLines { get; set; }
}

public sealed class SOrderLine
{
    public long Id { get; set; }
    public long SOrderId { get; set; }
    public string Product { get; set; } = string.Empty;
    [ServerOwned] public decimal UnitPrice { get; set; }
    public string Owner { get; set; } = string.Empty;
}

/// <summary>The fixes of the second audit of November 11, each against the way it was attacked.</summary>
public class SecondAuditTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();

    private static EntityRequestContext As(string user)
        => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test")));

    private IEntityStoreFactory Stores => new EntityStoreFactory(() => new MemoryStore(_database));

    // ---------- 1. An update keeps what the serializer leaves out ----------

    [Fact]
    public async Task An_update_keeps_the_values_the_client_never_had()
    {
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<SAccount>(Stores, EntityAccessPolicies.AllowAll));
        var dispatcher = new EntityRequestDispatcher(registry, _serializer, Stores);
        await new MemoryStore(_database).InsertAsync(new SAccount { Name = "ana", PasswordHash = "h1", Role = "admin" });
        var stored = Assert.Single(_database.All<SAccount>());

        // What a client holds: no hash (left out by the serializer), and it renames the account.
        var client = _serializer.Deserialize<SAccount>(_serializer.Serialize(stored))!;
        Assert.Equal(string.Empty, client.PasswordHash);
        client.Name = "ana2";
        var result = await dispatcher.UpdateAsync(EntityName.Of<SAccount>(), _serializer.Serialize(client), As("ana"));

        Assert.True(result.Successful, result.Exception);
        var after = Assert.Single(_database.All<SAccount>());
        Assert.Equal(("ana2", "h1", "admin"), (after.Name, after.PasswordHash, after.Role));
    }

    // ---------- 2. A hidden property the serializer does not honor ----------

    /// <summary>A serializer that honors [IgnoreMember] only, as MessagePack does.</summary>
    private sealed class MessagePackLike(JsonTestSerializer json) : IEntitySerializer
    {
        public byte[] Serialize<T>(T value) => json.Serialize(value);
        public T? Deserialize<T>(byte[] data) => json.Deserialize<T>(data);
        public bool Omits(PropertyInfo property) => RemoteMembers.Has(property, "IgnoreMemberAttribute");
    }

    [Fact]
    public async Task A_type_whose_hidden_property_the_serializer_would_send_is_not_served()
    {
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<SAccount>(Stores, EntityAccessPolicies.AllowAll));
        var logged = new List<string>();
        var dispatcher = new EntityRequestDispatcher(registry, new MessagePackLike(_serializer), Stores)
        {
            UnexpectedError = (error, _) => logged.Add(error.Message),
        };
        await new MemoryStore(_database).InsertAsync(new SAccount { Name = "ana", PasswordHash = "h1" });

        var read = await dispatcher.GetAllAsync(EntityName.Of<SAccount>(), As("ana"));

        Assert.False(read.Successful);
        Assert.Equal(EntityErrorCode.NotSupported, read.ErrorCode);
        Assert.DoesNotContain("h1", read.Exception ?? string.Empty);
        Assert.Contains(logged, m => m.Contains("SAccount.PasswordHash", StringComparison.Ordinal) && m.Contains("[IgnoreMember]", StringComparison.Ordinal));
    }

    [Fact]
    public void MessagePack_leaves_out_IgnoreMember_and_never_JsonIgnore()
    {
        var serializer = MessagePackEntitySerializer.Default;
        Assert.False(serializer.Omits(typeof(SAccount).GetProperty(nameof(SAccount.PasswordHash))!));
        Assert.True(_serializer.Omits(typeof(SAccount).GetProperty(nameof(SAccount.PasswordHash))!));
    }

    // ---------- 3. The children of a graph follow the rules of their own type ----------

    private RemoteEntityStore Client(IEntityAccessPolicy? linePolicy, RowLevelSecurity<SOrderLine>? lineRows = null, string user = "ana")
    {
        var graph = EntityGraph.For<SOrder, long>(o => o.Id)
            .HasMany(o => o.SOrderLines, foreignKey: l => l.SOrderId, key: l => l.Id);
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<SOrder>(Stores, EntityAccessPolicies.AllowAll,
            aggregate: new GraphRepository<SOrder, long>(Stores, graph)));
        if (linePolicy is not null)
            registry.Register(new EntityHandler<SOrderLine>(Stores, linePolicy, rows: lineRows));
        var dispatcher = new EntityRequestDispatcher(registry, _serializer, Stores);
        return new RemoteEntityStore(dispatcher.AsTransport(As(user)), _serializer);
    }

    [Fact]
    public async Task A_child_price_sent_by_the_client_is_reset_on_insert_and_kept_on_update()
    {
        await using var store = Client(linePolicy: null);     // the line type is not exposed: the server still owns its price
        var order = new SOrder { Customer = "ana", SOrderLines = [new SOrderLine { Product = "tea", UnitPrice = 0.01m }] };
        await store.InsertAsync(order);
        Assert.Equal(0m, Assert.Single(_database.All<SOrderLine>()).UnitPrice);

        // The server sets the price; the client sends the line back with a price of its own.
        var line = _database.All<SOrderLine>()[0];
        line.UnitPrice = 12.5m;
        await new MemoryStore(_database).UpdateAsync(line);
        order.SOrderLines![0].UnitPrice = 0.01m;
        order.SOrderLines[0].Product = "green tea";
        Assert.True(await store.UpdateAsync(order));

        var after = Assert.Single(_database.All<SOrderLine>());
        Assert.Equal(("green tea", 12.5m), (after.Product, after.UnitPrice));
    }

    [Fact]
    public async Task A_child_type_exposed_read_only_cannot_be_written_through_its_parent()
    {
        await using var store = Client(EntityAccessPolicies.ReadOnly);
        var order = new SOrder { Customer = "ana", SOrderLines = [new SOrderLine { Product = "tea" }] };

        var error = await Assert.ThrowsAsync<EntityStoreException>(() => store.InsertAsync(order));

        Assert.Equal(EntityErrorCode.Forbidden, error.ErrorCode);
        Assert.Empty(_database.All<SOrder>());                // the whole graph, in one transaction
        Assert.Empty(_database.All<SOrderLine>());
    }

    [Fact]
    public async Task Removing_a_parent_checks_the_deletion_of_each_child()
    {
        await new MemoryStore(_database).InsertAsync(new SOrder { Customer = "ana" });
        var order = Assert.Single(_database.All<SOrder>());
        await new MemoryStore(_database).InsertAsync(new SOrderLine { SOrderId = order.Id, Product = "tea" });
        var noDelete = EntityAccessPolicies.From(r => r.Operation != EntityOperation.Delete);
        await using var store = Client(noDelete);

        await Assert.ThrowsAsync<EntityStoreException>(() => store.DeleteAsync(order));

        Assert.Single(_database.All<SOrder>());
        Assert.Single(_database.All<SOrderLine>());
    }

    [Fact]
    public async Task A_child_of_another_user_is_out_of_reach_through_a_parent()
    {
        var lineRows = RowLevelSecurity.For<SOrderLine, long>(l => l.Id, user =>
        {
            var name = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return l => l.Owner == name;
        });
        await new MemoryStore(_database).InsertAsync(new SOrder { Customer = "shared" });
        var order = Assert.Single(_database.All<SOrder>());
        await new MemoryStore(_database).InsertAsync(new SOrderLine { SOrderId = order.Id, Product = "bob's", Owner = "bob" });
        await using var store = Client(EntityAccessPolicies.AllowAll, lineRows, user: "ana");

        // Ana loads nothing of Bob's, but sends the order with Bob's line rewritten as hers.
        var bobs = _database.All<SOrderLine>()[0];
        order.SOrderLines = [new SOrderLine { Id = bobs.Id, SOrderId = order.Id, Product = "mine now", Owner = "ana" }];
        var error = await Assert.ThrowsAsync<EntityStoreException>(() => store.UpdateAsync(order));

        Assert.Equal(EntityErrorCode.NotFound, error.ErrorCode);
        Assert.Equal("bob's", Assert.Single(_database.All<SOrderLine>()).Product);
    }

    [Fact]
    public async Task A_child_of_another_parent_cannot_be_moved_under_this_one()
    {
        await new MemoryStore(_database).InsertAsync(new SOrder { Customer = "bob" });
        await new MemoryStore(_database).InsertAsync(new SOrder { Customer = "ana" });
        var (bobs, anas) = (_database.All<SOrder>()[0], _database.All<SOrder>()[1]);
        await new MemoryStore(_database).InsertAsync(new SOrderLine { SOrderId = bobs.Id, Product = "bob's" });
        var line = Assert.Single(_database.All<SOrderLine>());
        await using var store = Client(linePolicy: null);

        anas.SOrderLines = [new SOrderLine { Id = line.Id, Product = "mine now" }];
        var error = await Assert.ThrowsAsync<EntityStoreException>(() => store.UpdateAsync(anas));

        Assert.Equal(EntityErrorCode.Conflict, error.ErrorCode);       // EntityGraph: it does not belong to this order
        var after = Assert.Single(_database.All<SOrderLine>());
        Assert.Equal((bobs.Id, "bob's"), (after.SOrderId, after.Product));
    }

    // ---------- 9. The database numbers a row inserted by a client ----------

    [Fact]
    public async Task A_numeric_key_sent_with_an_insert_is_not_kept()
    {
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<SAccount>(Stores, EntityAccessPolicies.AllowAll));
        var dispatcher = new EntityRequestDispatcher(registry, _serializer, Stores);
        await using var store = new RemoteEntityStore(dispatcher.AsTransport(As("ana")), _serializer);

        await store.InsertAsync(new SAccount { Id = 1_000_000, Name = "ana" });

        Assert.NotEqual(1_000_000, Assert.Single(_database.All<SAccount>()).Id);
    }
}

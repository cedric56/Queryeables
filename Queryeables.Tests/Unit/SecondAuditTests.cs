using System.Reflection;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Queryeables.Abstraction;
using Queryeables.MagicOnion;
using Queryeables.Remote;

namespace Queryeables.Tests.Unit;

/// <summary>An account whose hash never leaves the server.</summary>
public sealed class SAccount
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    [JsonIgnore] public string PasswordHash { get; set; } = string.Empty;
    public string? Role { get; set; }
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
    public decimal UnitPrice { get; set; }
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

    // ---------- 2. A hidden property the serializer does not honor ----------

    /// <summary>A serializer that honors [IgnoreMember] only, as MessagePack does.</summary>
    private sealed class MessagePackLike(JsonTestSerializer json) : IEntitySerializer
    {
        public byte[] Serialize<T>(T value) => json.Serialize(value);
        public T? Deserialize<T>(byte[] data) => json.Deserialize<T>(data);
        public bool Omits(PropertyInfo property) => RemoteMembers.Has(property, "IgnoreMemberAttribute");
    }

    [Fact]
    public void MessagePack_leaves_out_IgnoreMember_and_never_JsonIgnore()
    {
        var serializer = MessagePackEntitySerializer.Default;
        Assert.False(serializer.Omits(typeof(SAccount).GetProperty(nameof(SAccount.PasswordHash))!));
        Assert.True(_serializer.Omits(typeof(SAccount).GetProperty(nameof(SAccount.PasswordHash))!));
    }

    // ---------- 3. The children of a graph follow the rules of their own type ----------

    // ---------- 9. The database numbers a row inserted by a client ----------

}

using System.Reflection;
using Queryeables.Abstraction;
using Queryeables.MagicOnion;
using Queryeables.Remote;

namespace Queryeables.Tests.Unit;

/// <summary>A value hidden from the clients stays hidden by the serializer in use, or the server refuses to start.</summary>
public class SerializerCheckTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();

    private IEntityStoreFactory Stores => new EntityStoreFactory(() => new MemoryStore(_database));

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
        registry.Register(new EntityHandler<Account>(Stores, EntityAccessPolicies.AllowAll));
        registry.Register(new EntityHandler<TestClient>(Stores, EntityAccessPolicies.AllowAll));

        new EntityRequestDispatcher(registry, _serializer).CheckConfiguration();     // JSON honors [JsonIgnore]
        var error = Assert.Throws<InvalidOperationException>(
            () => new EntityRequestDispatcher(registry, new MessagePackLike(_serializer)).CheckConfiguration());
        Assert.Contains("Account.PasswordHash", error.Message);
    }

    [Fact]
    public void MessagePack_leaves_out_IgnoreMember_and_never_JsonIgnore()
    {
        var serializer = MessagePackEntitySerializer.Default;
        Assert.False(serializer.Omits(typeof(Account).GetProperty(nameof(Account.PasswordHash))!));
        Assert.True(_serializer.Omits(typeof(Account).GetProperty(nameof(Account.PasswordHash))!));
    }
}

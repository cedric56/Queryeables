using Queryeables.Abstraction;
using Queryeables.Onion;

namespace Queryeables.Tests.Unit;

public class EntityNameTests
{
    [RemoteName("Client")]
    public class Renamed;

    public class Plain;

    public static class OtherNamespace
    {
        public class Plain;
    }

    [Fact]
    public void Default_name_is_the_full_name()
        => Assert.Equal(typeof(Plain).FullName, EntityName.Of<Plain>());

    [Fact]
    public void Attribute_overrides_the_name()
        => Assert.Equal("Client", EntityName.Of<Renamed>());

    [Fact]
    public void Same_short_name_in_two_places_does_not_collide()
        => Assert.NotEqual(EntityName.Of<Plain>(), EntityName.Of<OtherNamespace.Plain>());

    [Fact]
    public void Registry_rejects_a_name_used_by_another_type()
    {
        var registry = new EntityHandlerRegistry();
        registry.Register("Client", new EntityHandler<TestClient>(() => throw new NotSupportedException(), EntityAccessPolicies.AllowAll));

        Assert.Throws<InvalidOperationException>(() =>
            registry.Register("Client", new EntityHandler<Renamed>(() => throw new NotSupportedException(), EntityAccessPolicies.AllowAll)));
    }

    [Fact]
    public void Registry_allows_replacing_the_handler_of_the_same_type()
    {
        var registry = new EntityHandlerRegistry();
        var second = new EntityHandler<TestClient>(() => throw new NotSupportedException(), EntityAccessPolicies.AllowAll);
        registry.Register(new EntityHandler<TestClient>(() => throw new NotSupportedException(), EntityAccessPolicies.AllowAll));
        registry.Register(second);

        Assert.True(registry.TryGet(EntityName.Of<TestClient>(), out var handler));
        Assert.Same(second, handler);
    }
}

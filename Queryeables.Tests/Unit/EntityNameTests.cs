using Queryeables.Abstraction;
using Queryeables.Remote;

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

}

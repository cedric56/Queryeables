using Queryeables.Onion;

namespace Queryeables.Server;

public static class QueryeablesBuilderExtensions
{
    /// <summary>
    /// Makes this application a Queryeables server for MagicOnion clients: <see cref="EntityServerExtensions.AddEntityServer"/>
    /// with MessagePack. Map the service with <c>app.MapMagicOnionService()</c>, through a class deriving from
    /// <see cref="MagicOnionService"/>.
    /// </summary>
    public static QueryeablesBuilder AddMagicOnionServer(this QueryeablesBuilder builder, Action<EntityServerBuilder> expose)
        => builder.AddEntityServer(MessagePackEntitySerializer.Default, expose);
}

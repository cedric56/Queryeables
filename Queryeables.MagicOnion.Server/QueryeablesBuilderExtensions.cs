using Queryeables.MagicOnion;

namespace Queryeables.MagicOnion.Server;

public static class QueryeablesBuilderExtensions
{
    /// <summary>
    /// Makes this application a Queryeables server for MagicOnion clients: <see cref="EntityServerExtensions.AddEntityServer"/>
    /// with MessagePack, reading what clients send as untrusted data (<see cref="MessagePackEntitySerializer.Server"/>).
    /// Map the service with <c>app.MapMagicOnionService()</c>, through a class deriving from <see cref="MagicOnionService"/>.
    /// For the MagicOnion messages themselves, set <c>MessagePackSecurity.UntrustedData</c> in the options of
    /// <c>AddMagicOnion</c>.
    /// </summary>
    public static QueryeablesBuilder AddMagicOnionServer(this QueryeablesBuilder builder, Action<EntityServerBuilder> expose)
        => builder.AddEntityServer(MessagePackEntitySerializer.Server, expose);
}

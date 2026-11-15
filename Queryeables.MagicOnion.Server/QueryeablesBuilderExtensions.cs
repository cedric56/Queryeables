using Microsoft.Extensions.DependencyInjection;
using Queryeables.MagicOnion;
using Queryeables.MagicOnion.Server;
using Queryeables.Remote;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables;

/// <summary>The MagicOnion server of <see cref="QueryeablesBuilder"/>: in the namespace of the builder, found without a using.</summary>
public static class MagicOnionServerQueryeablesBuilderExtensions
{
    /// <summary>
    /// Makes this application a Queryeables server for MagicOnion clients: registers MagicOnion (<c>AddMagicOnion</c>) and
    /// <see cref="EntityServerExtensions.AddEntityServer"/> with the JSON entity serializer of the clients
    /// (<see cref="JsonEntitySerializer"/>). Then map the service: <c>app.MapQueryeables()</c>.
    /// </summary>
    /// <remarks>For Native AOT, use the overload that takes a serializer built on a <c>JsonSerializerContext</c>.</remarks>
    [RequiresUnreferencedCode("JSON by reflection: pass a JsonEntitySerializer built on a JsonSerializerContext for trimming.")]
    [RequiresDynamicCode("JSON by reflection: pass a JsonEntitySerializer built on a JsonSerializerContext for Native AOT.")]
    public static QueryeablesBuilder AddMagicOnionServer(this QueryeablesBuilder builder, Action<EntityServerBuilder> expose)
        => builder.AddMagicOnionServer(JsonEntitySerializer.Default, expose);

    /// <inheritdoc cref="AddMagicOnionServer(QueryeablesBuilder, Action{EntityServerBuilder})"/>
    /// <param name="serializer">
    /// The entity serializer, the same as the clients': a <see cref="JsonEntitySerializer"/> on a source-generated context
    /// (Native AOT), or <see cref="MessagePackEntitySerializer.Server"/> for entities marked <c>[MessagePackObject]</c>
    /// (it reads what clients send as untrusted data). For the MagicOnion messages themselves, set
    /// <c>MessagePackSecurity.UntrustedData</c> in the options of <c>AddMagicOnion</c>.
    /// </param>
    public static QueryeablesBuilder AddMagicOnionServer(this QueryeablesBuilder builder, IEntitySerializer serializer,
        Action<EntityServerBuilder> expose)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddMagicOnion();                  // does nothing when already added
        return builder.AddEntityServer(serializer, expose);
    }
}

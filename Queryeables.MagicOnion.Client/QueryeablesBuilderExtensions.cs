using MagicOnion.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Queryeables.Abstraction;
using Queryeables.Remote;
using System.Diagnostics.CodeAnalysis;
using Queryeables.MagicOnion;
using Queryeables.MagicOnion.Client;

namespace Queryeables;

/// <summary>The MagicOnionClient provider of <see cref="QueryeablesBuilder"/>: in the namespace of the builder, found without a using.</summary>
public static class MagicOnionClientQueryeablesBuilderExtensions
{
    /// <summary>
    /// Stores that reach a Queryeables server over MagicOnion (gRPC through the WebSocket bridge, so it also works in the
    /// browser). Registers <see cref="IMagicOnionLayer"/>, the entity serializer (JSON: <see cref="JsonEntitySerializer"/>, no
    /// attribute on the classes) and remote stores: the same caches and services as locally, served by the server.
    /// </summary>
    /// <param name="url">Address of the server, for example <c>https://server:5001</c>.</param>
    /// <param name="validateCertificate">Custom certificate validation, for example a known self-signed certificate.</param>
    /// <remarks>For Native AOT, use the overload that takes a serializer built on a <c>JsonSerializerContext</c>.</remarks>
    [RequiresUnreferencedCode("JSON by reflection: pass a JsonEntitySerializer built on a JsonSerializerContext for trimming.")]
    [RequiresDynamicCode("JSON by reflection: pass a JsonEntitySerializer built on a JsonSerializerContext for Native AOT.")]
    public static QueryeablesBuilder UseMagicOnion(this QueryeablesBuilder builder, string url,
        CertificateValidator? validateCertificate = null)
        => builder.UseMagicOnion(url, JsonEntitySerializer.Default, validateCertificate);

    /// <inheritdoc cref="UseMagicOnion(QueryeablesBuilder, string, CertificateValidator?)"/>
    /// <param name="serializer">
    /// The entity serializer, the same as the server's: a <see cref="JsonEntitySerializer"/> on a source-generated context
    /// (Native AOT), or <see cref="MessagePackEntitySerializer.Default"/> for entities marked <c>[MessagePackObject]</c>.
    /// </param>
    public static QueryeablesBuilder UseMagicOnion(this QueryeablesBuilder builder, string url, IEntitySerializer serializer,
        CertificateValidator? validateCertificate = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(serializer);

        builder.Services.TryAddSingleton(_ =>
            MagicOnionClient.Create<IMagicOnionLayer>(MagicOnionClientExtensions.CreateChannel(url, validateCertificate: validateCertificate)));
        builder.Services.TryAddSingleton(serializer);
        builder.Services.TryAddSingleton<IEntityTransport>(sp => new MagicOnionTransport(sp.GetRequiredService<IMagicOnionLayer>()));
        return builder.UseStores(sp => RemoteEntityStore.Factory(
            sp.GetRequiredService<IEntityTransport>(),
            sp.GetRequiredService<IEntitySerializer>(),
            sp.GetService<IChangeSession>()));
    }
}

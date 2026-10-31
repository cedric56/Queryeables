using MagicOnion.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Queryeables.Abstraction;
using Queryeables.Remote;

namespace Queryeables.Onion.Client;

public static class QueryeablesBuilderExtensions
{
    /// <summary>
    /// Stores that reach a Queryeables server over MagicOnion (gRPC through the WebSocket bridge, so it also works in the
    /// browser). Registers <see cref="IMagicOnionLayer"/>, the MessagePack <see cref="IEntitySerializer"/> and remote stores:
    /// the same caches and services as locally, served by the server.
    /// </summary>
    /// <param name="url">Address of the server, for example <c>https://server:5001</c>.</param>
    /// <param name="handler">Custom HTTP handler; replaces the WebSocket bridge.</param>
    /// <param name="validateCertificate">Custom certificate validation, for example a known self-signed certificate.</param>
    public static QueryeablesBuilder UseMagicOnion(this QueryeablesBuilder builder, string url,
        HttpMessageHandler? handler = null, CertificateValidator? validateCertificate = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        builder.Services.TryAddSingleton(_ =>
            MagicOnionClient.Create<IMagicOnionLayer>(MagicOnionClientExtensions.CreateChannel(url, handler, validateCertificate)));
        builder.Services.TryAddSingleton<IEntitySerializer>(MessagePackEntitySerializer.Default);
        builder.Services.TryAddSingleton<IEntityTransport>(sp => new MagicOnionTransport(sp.GetRequiredService<IMagicOnionLayer>()));
        return builder.UseStores(sp => RemoteEntityStore.Factory(
            sp.GetRequiredService<IEntityTransport>(),
            sp.GetRequiredService<IEntitySerializer>(),
            sp.GetService<IChangeSession>()));
    }
}

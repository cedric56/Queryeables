using MagicOnion.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using Queryeables.Onion;
using Queryeables.Onion.Client;
using Queryeables.Remote;
using Queryeables.Server;
using System.Net;

namespace Queryeables.Benchmarks;

/// <summary>The MagicOnion service of the benchmark server.</summary>
public sealed class BenchmarkLayerService(EntityRequestDispatcher dispatcher) : MagicOnionService(dispatcher);

/// <summary>
/// A real Queryeables server, in this process but across a real connection: Kestrel on 127.0.0.1 (a free port), gRPC over
/// HTTP/2, MagicOnion and MessagePack, as an application deploys it, the network latency of a loopback excepted.
/// </summary>
public sealed class LoopbackServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<IDisposable> _channels = [];

    private LoopbackServer(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    /// <summary>Where the server listens, such as <c>http://127.0.0.1:53124</c>.</summary>
    public string Url { get; }

    /// <summary>Starts a server that exposes <see cref="Item"/> from <paramref name="stores"/>, to everyone.</summary>
    public static async Task<LoopbackServer> StartAsync(IEntityStoreFactory stores)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));   // gRPC without TLS (h2c)
        builder.Services.AddMagicOnion();
        builder.Services.UseQueryeables(q => q
            .UseStores(_ => stores)
            .AddMagicOnionServer(server => server.Expose<Item>(EntityAccessPolicies.AllowAll)));

        var app = builder.Build();
        app.MapMagicOnionService(typeof(BenchmarkLayerService));
        await app.StartAsync();
        return new LoopbackServer(app, app.Urls.First());
    }

    /// <summary>A client of this server, on a channel of its own: what <c>q.UseMagicOnion(url)</c> registers.</summary>
    public IEntityStoreFactory Client()
    {
        var channel = MagicOnionClientExtensions.CreateChannel(Url);
        _channels.Add(channel);
        var transport = new MagicOnionTransport(MagicOnionClient.Create<IMagicOnionLayer>(channel));
        return RemoteEntityStore.Factory(transport, MessagePackEntitySerializer.Default);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var channel in _channels)
            channel.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

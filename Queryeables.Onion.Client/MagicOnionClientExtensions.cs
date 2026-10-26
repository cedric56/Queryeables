using Grpc.Net.Client;
using GrpcWebSocketBridge.Client;
using MagicOnion;
using MagicOnion.Client;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Queryeables.Onion.Client;

/// <summary>
/// Validates the server certificate; return true to accept it.
/// </summary>
public delegate bool CertificateValidator(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors);

/// <summary>
/// MagicOnion client helpers: gRPC channel creation (WebSocket bridge, usable in the browser) and subscription of a
/// monitor to the change hub (<see cref="IRecordHub{TObject}"/>).
/// </summary>
/// <example>
/// <code>
/// // Services
/// services.AddSingleton(sp => sp.Create&lt;IMagicOnionLayer&gt;(Constants.ServerUrl));
///
/// // Change notifications for Person
/// var unsubscribe = await monitor.AddStreamingHub(Constants.ServerUrl, session, logger: logger);
/// ...
/// await unsubscribe();
/// </code>
/// </example>
public static class MagicOnionClientExtensions
{
    /// <summary>
    /// Creates a gRPC channel to <paramref name="url"/>. By default, the channel goes through the gRPC WebSocket bridge
    /// (works everywhere, browser included) and the server certificate is validated as usual.
    /// </summary>
    /// <param name="handler">Custom HTTP handler; replaces the WebSocket bridge.</param>
    /// <param name="validateCertificate">
    /// Custom certificate validation, for example to accept a known self-signed certificate by its hash.
    /// Ignored in the browser, where the browser validates the certificate.
    /// </param>
    public static GrpcChannel CreateChannel(string url, HttpMessageHandler? handler = null,
        CertificateValidator? validateCertificate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (handler is null)
        {
            var bridge = new GrpcWebSocketBridgeHandler();
            if (validateCertificate is not null && !OperatingSystem.IsBrowser()
                && bridge.InnerHandler is HttpClientHandler http)
            {
                http.ServerCertificateCustomValidationCallback = (_, certificate, chain, errors)
                    => validateCertificate(certificate, chain, errors);
            }
            handler = bridge;
        }

        return GrpcChannel.ForAddress(url, new GrpcChannelOptions { HttpHandler = handler });
    }

    /// <summary>Creates a MagicOnion service client, for example <see cref="IMagicOnionLayer"/>, on a new channel.</summary>
    public static TService Create<TService>(this IServiceProvider provider, string url,
        HttpMessageHandler? handler = null, CertificateValidator? validateCertificate = null)
        where TService : IService<TService>
        => MagicOnionClient.Create<TService>(CreateChannel(url, handler, validateCertificate));

    /// <summary>
    /// Subscribes <paramref name="monitor"/> to the MagicOnion change hub of <typeparamref name="TObject"/>:
    /// changes received from the server are published on the monitor (except this client's own changes),
    /// and local changes are sent to the server.
    /// </summary>
    /// <param name="session">Receives the hub session id, sent with writes so that this client ignores its own changes.</param>
    /// <param name="registry">When provided, the monitor is added to it to receive the local database changes.</param>
    /// <returns>
    /// Function that leaves the hub and detaches the monitor. When the server is unreachable, the error is logged and
    /// a function that does nothing is returned; call this method again later to retry.
    /// </returns>
    /// <remarks>
    /// The streaming hub does not reconnect by itself. After a reconnection, call <see cref="EntityCache{TEntity, TKey}.SyncAsync"/>
    /// on the caches fed by this monitor to catch up with the changes missed in the meantime.
    /// </remarks>
    public static async Task<Func<Task>> AddStreamingHub<TObject>(
        this IDbMonitor<TObject> monitor,
        string url,
        IDbSession session,
        HttpMessageHandler? handler = null,
        ILogger? logger = null,
        DbMonitorRegistry? registry = null,
        CertificateValidator? validateCertificate = null)
        where TObject : class
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(session);

        if (session.TryGetSession(typeof(TObject), out var existing))
            return existing.unsubscribe;

        var channel = CreateChannel(url, handler, validateCertificate);
        IRecordHub<TObject>? hub = null;
        var receiver = new RecordHubReceiver<TObject>(monitor, session,
            send: record =>
            {
                hub?.OnRecordChanged(RecordMessage.From(record));
                return Task.CompletedTask;
            },
            onError: ex => logger?.LogWarning(ex, "Could not send the {Type} change to the hub.", typeof(TObject).Name));

        try
        {
            hub = await StreamingHubClient.ConnectAsync<IRecordHub<TObject>, IRecordHubReceiver<TObject>>(channel, receiver);
            var id = await hub.AddReceiverAsync();
            registry?.Add(monitor);
            session.SetSession(typeof(TObject), (id.ToString(), UnsubscribeAsync));
            return UnsubscribeAsync;
        }
        catch (Exception ex)
        {
            receiver.Dispose();
            channel.Dispose();
            logger?.LogError(ex, "Could not connect to the hub {Url} for {Type}.", url, typeof(TObject).Name);
            return () => Task.CompletedTask;
        }

        async Task UnsubscribeAsync()
        {
            receiver.Dispose();
            registry?.Remove(monitor);
            session.RemoveSession(typeof(TObject));
            try
            {
                await hub!.RemoveAsync();                       // tell the server we are leaving
                var disconnected = hub.WaitForDisconnectAsync();  // captured before disposing
                await hub.DisposeAsync();                       // triggers the disconnection
                await disconnected;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Incomplete disconnection from the hub {Url}.", url);
            }
            finally
            {
                channel.Dispose();
            }
        }
    }
}

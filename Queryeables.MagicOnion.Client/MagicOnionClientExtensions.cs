using Grpc.Net.Client;
using GrpcWebSocketBridge.Client;
using MagicOnion;
using MagicOnion.Client;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using Queryeables.Remote;

namespace Queryeables.MagicOnion.Client;


/// <summary>
/// MagicOnion client helpers: gRPC channel creation (WebSocket bridge, usable in the browser) and subscription of a
/// change feed to the change hub (<see cref="IChangeHub{TObject}"/>).
/// </summary>
/// <example>
/// <code>
/// // Services
/// services.AddSingleton(sp => sp.Create&lt;IMagicOnionLayer&gt;(Constants.ServerUrl));
///
/// // Change notifications for Person
/// var unsubscribe = await change feed.AddStreamingHub(Constants.ServerUrl, session, logger: logger);
/// ...
/// await unsubscribe();
/// </code>
/// </example>
public static class MagicOnionClientExtensions
{
    /// <summary>
    /// Creates a gRPC channel to <paramref name="url"/>: through the gRPC WebSocket bridge in the browser, over HTTP/2
    /// (<see cref="SocketsHttpHandler"/>) elsewhere. The server certificate is validated by the system, on every platform,
    /// Android included, unless <paramref name="validateCertificate"/> is given.
    /// </summary>
    /// <param name="handler">The handler to use instead: a <see cref="SocketsHttpHandler"/> or an <see cref="HttpClientHandler"/>
    /// when <paramref name="validateCertificate"/> is given.</param>
    /// <param name="validateCertificate">
    /// Custom certificate validation, for example to accept a known self-signed certificate by its hash: it replaces the
    /// system's decision. Ignored in the browser, where the browser validates the certificate.
    /// </param>
    /// <exception cref="NotSupportedException">A validator with a handler that cannot use it.</exception>
    public static GrpcChannel CreateChannel(
        string url,
        HttpMessageHandler? handler = null,
        CertificateValidator? validateCertificate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (OperatingSystem.IsBrowser())
        {
            handler ??= new GrpcWebSocketBridgeHandler();
        }
        else
        {
            handler ??= new SocketsHttpHandler();
            if (validateCertificate is not null)
                CertificateValidation.Attach(handler, validateCertificate);
        }

        return GrpcChannel.ForAddress(
            url,
            new GrpcChannelOptions
            {
                HttpHandler = handler
            });
    }

    /// <summary>Creates a MagicOnion service client, for example <see cref="IMagicOnionLayer"/>, on a new channel.</summary>
    public static TService Create<TService>(this IServiceProvider provider, string url, CertificateValidator? validateCertificate = null)
        where TService : IService<TService>
        => MagicOnionClient.Create<TService>(CreateChannel(url, validateCertificate: validateCertificate));

    /// <summary>
    /// Subscribes <paramref name="monitor"/> to the MagicOnion change hub of <typeparamref name="TObject"/>:
    /// changes received from the server are published on the change feed (except this client's own changes),
    /// and local changes are sent to the server.
    /// </summary>
    /// <param name="session">Receives the hub session id, sent with writes so that this client ignores its own changes.</param>
    /// <param name="registry">When provided, the change feed is added to it to receive the local database changes.</param>
    /// <returns>
    /// Function that leaves the hub and detaches the change feed. When the server is unreachable, the error is logged and
    /// a function that does nothing is returned; call this method again later to retry.
    /// </returns>
    /// <remarks>
    /// The streaming hub does not reconnect by itself. After a reconnection, call <see cref="EntityCache{TEntity, TKey}.SyncAsync"/>
    /// on the caches fed by this change feed to catch up with the changes missed in the meantime.
    /// </remarks>
    public static async Task<Func<Task>> AddStreamingHub<TObject>(
        this IChangeFeed<TObject> monitor,
        string url,
        IChangeSession session,
        ILogger? logger = null,
        ChangeFeedRegistry? registry = null,
        CertificateValidator? validateCertificate = null)
        where TObject : class
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(session);

        if (session.TryGetSession(typeof(TObject), out var existing))
            return existing.unsubscribe;

        var channel = CreateChannel(url,validateCertificate: validateCertificate);
        IChangeHub<TObject>? hub = null;
        var receiver = new ChangeHubReceiver<TObject>(monitor, session,
            send: record =>
            {
                hub?.OnChangeReceived(ChangeMessage.From(record));
                return Task.CompletedTask;
            },
            onError: ex => logger?.LogWarning(ex, "Could not send the {Type} change to the hub.", typeof(TObject).Name));

        try
        {
            hub = await StreamingHubClient.ConnectAsync<IChangeHub<TObject>, IChangeHubReceiver<TObject>>(channel, receiver);
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

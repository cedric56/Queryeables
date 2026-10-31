using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables.SignalR;

public static class SignalRExtensions
{
    /// <summary>
    /// Connects a change feed to a SignalR hub:
    /// changes received from the hub are published on the change feed, and local changes are sent to the hub.
    /// </summary>
    /// <param name="session">Receives the connection id, which changes on every reconnection.</param>
    /// <param name="factory">Custom HTTP handler, for example for SSL validation on Android.</param>
    /// <param name="registry">When provided, the change feed is added to it to receive the local database changes.</param>
    /// <returns>Function that stops the connection and detaches the change feed.</returns>
    /// <remarks>
    /// When the server is unreachable at startup, the method does not throw:
    /// it retries in the background according to <paramref name="retryPolicy"/>.
    /// </remarks>
    [RequiresUnreferencedCode(
    "This SignalR configuration uses APIs that may not be compatible with trimming.")]
    public static async Task<Func<Task>> AddSignalR<[DynamicallyAccessedMembers(EntityMembers.Required)] TObject>(
        this IChangeFeed<TObject> monitor,
        string url,
        IChangeSession session,
        IRetryPolicy? retryPolicy = null,
        Func<HttpMessageHandler, HttpMessageHandler>? factory = null,
        ILogger? logger = null,
        ChangeFeedRegistry? registry = null)
        where TObject : class
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(session);

        var policy = retryPolicy ?? new FiveSecondsReconnectPolicy();
        var stop = new CancellationTokenSource();

        var hub = new HubConnectionBuilder()
            .AddMessagePackProtocol()
            .WithUrl(url, options =>
            {
                if (OperatingSystem.IsBrowser())
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.SkipNegotiation = false; // keep negotiation, unless you are sure WebSockets alone is enough
                }

                if (factory != null)
                    options.HttpMessageHandlerFactory = factory;
            })
            .WithAutomaticReconnect(policy)
            .Build();

        // Logic shared by the transports: ignore our own changes, never send back those received from the hub.
        var relay = new ChangeRelay<TObject>(monitor,
            connectionId: () => hub.State == HubConnectionState.Connected ? hub.ConnectionId : null,
            send: record => hub.InvokeAsync(SignalRContract.OnChangeReceived, SignalRChange.From(record), stop.Token),
            onError: ex =>
            {
                if (!stop.IsCancellationRequested)
                    logger?.LogWarning(ex, "Could not send the {Type} change to the hub.", typeof(TObject).Name);
            });

        hub.On<SignalRChange>(SignalRContract.DbChanged, message => relay.Receive(message.ToChange<TObject>()));

        registry?.Add(monitor);
        hub.Reconnected += OnReconnected;

        var error = await TryStartAsync();
        if (error is not null)
            _ = RetryStartAsync(error);

        return UnsubscribeAsync;

        async Task UnsubscribeAsync()
        {
            stop.Cancel();
            relay.Dispose();
            hub.Reconnected -= OnReconnected;
            registry?.Remove(monitor);
            session.RemoveSession(typeof(TObject));
            try
            {
                await hub.StopAsync();
            }
            finally
            {
                await hub.DisposeAsync();
            }
        }

        Task OnReconnected(string? connectionId)
        {
            RegisterSession(connectionId);
            return Task.CompletedTask;
        }

        void RegisterSession(string? connectionId)
        {
            if (connectionId is not null)
                session.SetSession(typeof(TObject), (connectionId, UnsubscribeAsync));
        }

        async Task<Exception?> TryStartAsync()
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await hub.StartAsync(timeout.Token);
                RegisterSession(hub.ConnectionId);
                return null;
            }
            catch (Exception ex)
            {
                if (!stop.IsCancellationRequested)
                    logger?.LogWarning(ex, "Could not connect to the hub {Url}.", url);
                return ex;
            }
        }

        // WithAutomaticReconnect only covers outages after a first successful connection.
        async Task RetryStartAsync(Exception firstError)
        {
            var started = DateTime.UtcNow;
            var lastError = firstError;

            for (long attempt = 0; !stop.IsCancellationRequested; attempt++)
            {
                var delay = policy.NextRetryDelay(new RetryContext
                {
                    PreviousRetryCount = attempt,
                    ElapsedTime = DateTime.UtcNow - started,
                    RetryReason = lastError
                });

                if (delay is null)
                {
                    logger?.LogError(lastError, "Giving up connecting to the hub {Url}.", url);
                    return;
                }

                try
                {
                    await Task.Delay(delay.Value, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var error = await TryStartAsync();
                if (error is null)
                    return;
                lastError = error;
            }
        }
    }
}

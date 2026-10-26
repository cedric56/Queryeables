using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;

namespace Queryeables.SignalR;

public static class SignalRExtensions
{
    // Vrai pendant qu'un changement reçu du hub est publié localement : il ne doit pas repartir vers le serveur.
    private static readonly AsyncLocal<bool> _publishingFromHub = new();

    /// <summary>
    /// Relie un moniteur à un hub SignalR :
    /// les changements reçus du hub sont publiés sur le moniteur, et les changements locaux sont envoyés au hub.
    /// </summary>
    /// <param name="session">Reçoit l'identifiant de connexion, qui change à chaque reconnexion.</param>
    /// <param name="factory">Gestionnaire HTTP personnalisé, par exemple pour la validation SSL sur Android.</param>
    /// <param name="registry">Si fourni, le moniteur y est ajouté pour recevoir les changements de la base locale.</param>
    /// <returns>Fonction qui arrête la connexion et détache le moniteur.</returns>
    /// <remarks>
    /// Si le serveur est injoignable au démarrage, la méthode ne lève pas d'exception :
    /// elle réessaie en arrière-plan selon <paramref name="retryPolicy"/>.
    /// </remarks>
    public static async Task<Func<Task>> AddSignalR<TObject>(
        this DbMonitor<TObject> monitor,
        string url,
        IDbSession session,
        IRetryPolicy? retryPolicy = null,
        Func<HttpMessageHandler, HttpMessageHandler>? factory = null,
        ILogger? logger = null,
        DbMonitorRegistry? registry = null)
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
                    options.SkipNegotiation = false; // garder la négociation, sauf certitude que WebSockets seul suffit
                }

                if (factory != null)
                    options.HttpMessageHandlerFactory = factory;
            })
            .WithAutomaticReconnect(policy)
            .Build();

        hub.On<Record<TObject>>(SignalRContract.DbChanged, record =>
        {
            if (record.Contains(hub.ConnectionId))
                return;

            _publishingFromHub.Value = true;
            try
            {
                monitor.Publish(record);
            }
            finally
            {
                _publishingFromHub.Value = false;
            }
        });

        registry?.Add(monitor);
        monitor.RecordChanged += OnLocalChange;
        hub.Reconnected += OnReconnected;

        var error = await TryStartAsync();
        if (error is not null)
            _ = RetryStartAsync(error);

        return UnsubscribeAsync;

        async Task UnsubscribeAsync()
        {
            stop.Cancel();
            monitor.RecordChanged -= OnLocalChange;
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

        // async void imposé par l'événement : aucune exception ne doit en sortir.
        async void OnLocalChange(object? sender, Record<TObject> record)
        {
            // Un changement qui vient du hub n'y retourne pas : sinon il rebondirait de client en client.
            if (_publishingFromHub.Value)
                return;

            try
            {
                if (hub.State != HubConnectionState.Connected)
                    return;

                if (!record.Contains(hub.ConnectionId))
                    record.Add(hub.ConnectionId);

                await hub.InvokeAsync(SignalRContract.OnRecordChanged, record, stop.Token);
            }
            catch (Exception ex)
            {
                if (!stop.IsCancellationRequested)
                    logger?.LogWarning(ex, "Impossible d'envoyer le changement de {Type} au hub.", typeof(TObject).Name);
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
                    logger?.LogWarning(ex, "Connexion au hub {Url} impossible.", url);
                return ex;
            }
        }

        // WithAutomaticReconnect ne couvre que les coupures après une première connexion réussie.
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
                    logger?.LogError(lastError, "Abandon de la connexion au hub {Url}.", url);
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

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Queryeables.Abstraction;

public static class SignalRExtensions
{
    public const string DBMessage = "DBChanged";

    public static async Task<Func<Task>> AddSignalR<TObject>(
        this IDBMonitor<TObject> monitor,
        string url,
        IDBSession dbSession,
        IRetryPolicy? retryPolicy = null,
        Func<HttpMessageHandler, HttpMessageHandler>? factory = null,
        ILogger? logger = null,
        SignalRHub<TObject>? signalRHub = null)
        where TObject : class, new()
    {
        dbSession.AddMonitor(monitor);

        var hub = new HubConnectionBuilder()
            .AddMessagePackProtocol()
             //.WithServerTimeout(TimeSpan.FromSeconds(5))
             .WithUrl(url, options =>
             {
                 if (OperatingSystem.IsBrowser())
                 {
                     options.Transports = HttpTransportType.WebSockets;
                     options.SkipNegotiation = false; // keep negotiate unless you're sure WS-only is safe
                 }

                 // ✅ FIX: Configure HttpClient for Android SSL validation
                 if (factory != null)
                     options.HttpMessageHandlerFactory = factory;
             })
            .WithAutomaticReconnect(retryPolicy ?? new FiveSecondsReconnectPolicy())
            .Build();

        hub.On<Record<TObject>>(DBMessage, record =>
        {
            //we stop propagating to avoid stackoverflow
            if (record.Contains(hub.ConnectionId))
                return;

            monitor.OnChanged(record);
        });
        //monitor.Restart = TryConnect;
        monitor.OnRecordChanged += OnRecordChanged;
        hub.Closed += Closed;
        hub.Reconnected += Reconnected;
        var unsubscribe = async () =>
        {
            hub.Closed -= Closed;
            hub.Reconnected -= Reconnected;
            try
            {
                await hub.StopAsync();
                await hub.DisposeAsync();
            }
            finally
            {
                monitor.OnRecordChanged -= OnRecordChanged;
            }
        };
        await TryConnect();
        return unsubscribe;

        async void OnRecordChanged(object? sender, Record<TObject> e)
        {
            e.Add(hub.ConnectionId);

            if (hub.State == HubConnectionState.Connected)
                await hub.InvokeAsync(nameof(SignalRHub<TObject>.OnRecordChanged), e);

            //If an embedded server, we notify clients
            if (signalRHub is not null)
                signalRHub.OnRecordChanged(e);
        }

        async Task TryConnect()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await hub.StartAsync(cts.Token);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex.Message);
            }
            finally
            {
                dbSession.SetSession(typeof(TObject), (hub.ConnectionId ?? throw new InvalidOperationException("Connection must be known"), unsubscribe));
            }
        }

        Task Reconnected(string? value)
        {
            //monitor.IsRunning = hub.State == HubConnectionState.Connected;
            return Task.CompletedTask;
        }

        Task Closed(Exception? ex)
        {
            //monitor.IsRunning = hub.State == HubConnectionState.Connected;
            return Task.CompletedTask;
        }
    }
}

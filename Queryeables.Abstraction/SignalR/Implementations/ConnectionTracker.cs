using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Queryeables.Abstraction;

public class ConnectionTracker<TObject> : IDisposable
    where TObject : class, new()
{
    private readonly HashSet<string> connections = new();
    private readonly object gate = new();

    private readonly IHubContext<SignalRHub<TObject>> hubContext;
    private readonly IDBMonitor<TObject> monitor;
    private readonly ILogger? logger;

    private readonly Channel<Record<TObject>> notifications =
        Channel.CreateUnbounded<Record<TObject>>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

    private readonly CancellationTokenSource disposeCts = new();
    private readonly Task notificationLoop;

    private int disposed;

    public ConnectionTracker(
        IHubContext<SignalRHub<TObject>> hubContext,
        IDBMonitor<TObject> monitor,
        IDBSession dbSession,
        ILogger? logger = null)
    {
        this.logger = logger;
        this.monitor = monitor;
        this.hubContext = hubContext;

        dbSession.AddMonitor(monitor);

        monitor.OnRecordChanged += OnRecordChanged;

        notificationLoop = ProcessNotificationsAsync(disposeCts.Token);
    }

    public void Add(string connectionId)
    {
        lock (gate)
        {
            connections.Add(connectionId);
        }

        logger?.LogInformation(
            "Customer connected: {ConnectionId}",
            connectionId);
    }

    public void Remove(string connectionId)
    {
        lock (gate)
        {
            connections.Remove(connectionId);
        }

        logger?.LogInformation(
            "Customer disconnected: {ConnectionId}",
            connectionId);
    }

    public void OnRecordChanged(
        object? sender,
        Record<TObject> e)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;

        if (!notifications.Writer.TryWrite(e))
        {
            logger?.LogWarning(
                "Failed to queue database record notification.");
        }
    }

    private async Task ProcessNotificationsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var record in notifications.Reader.ReadAllAsync(
                cancellationToken))
            {
                try
                {
                    await HandleRecordChangedAsync(
                        record,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger?.LogError(
                        ex,
                        "Failed to notify clients about a record change.");
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger?.LogError(
                ex,
                "Notification processing loop failed.");
        }
    }

    private async Task HandleRecordChangedAsync(
        Record<TObject> e,
        CancellationToken cancellationToken)
    {
        var excludedIds = e.Connections ?? [];

        List<string> notified;

        lock (gate)
        {
            notified = connections
                .Where(id => !excludedIds.Contains(id))
                .ToList();
        }

        foreach (var id in notified)
        {
            logger?.LogInformation(
                "Connected: {ConnectionId}",
                id);
        }

        await hubContext.Clients
            .AllExcept(excludedIds)
            .SendAsync(
                SignalRExtensions.DBMessage,
                e,
                cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        monitor.OnRecordChanged -= OnRecordChanged;

        // No new notifications can be queued after this point.
        notifications.Writer.TryComplete();

        try
        {
            // Drain all notifications already queued.
            notificationLoop.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            disposeCts.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
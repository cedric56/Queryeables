using Avae.DAL;
using Avae.Onion;
using MagicOnion.Server.Hubs;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Avae.Server;

/// <summary>
/// Tracks which connections are subscribed to record change notifications for <typeparamref name="TObject"/>,
/// and forwards notifications from a monitored data source to the shared <see cref="IGroup{TReceiver}"/>.
/// </summary>
/// <typeparam name="TObject">The entity type this repository tracks change notifications for.</typeparam>
public class RecordHubRepository<TObject> : IDisposable where TObject : class, new()
{
    /// <summary>
    /// Set of connection identifiers currently subscribed to notifications. Only membership matters;
    /// the byte value is unused. The underlying broadcast group is shared across all connections.
    /// </summary>
    readonly Dictionary<Guid, byte> customerIds = new(); // just tracking membership, group itself is shared

    /// <summary>
    /// The shared broadcast group used to send change notifications, captured from the first registering connection.
    /// </summary>
    IGroup<IRecordHubReceiver<TObject>>? group;

    /// <summary>
    /// The data source monitor whose change events are forwarded to subscribed connections.
    /// </summary>
    IDBMonitor<TObject> monitor;

    /// <summary>
    /// Optional logger used to record registration, unregistration, and notification activity.
    /// </summary>
    ILogger? logger;

    /// <summary>
    /// Synchronizes access to <see cref="customerIds"/> and <see cref="group"/>.
    /// </summary>
    readonly object gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordHubRepository{TObject}"/> class, registering
    /// it with <see cref="IDBFactory.Monitors"/> and subscribing to the monitor's change events.
    /// </summary>
    /// <param name="monitor">The data source monitor to subscribe to for change notifications.</param>
    /// <param name="logger">Optional logger for registration and notification activity.</param>
    public RecordHubRepository(IDBMonitor<TObject> monitor, IDBSession session, ILogger? logger = null)
    {
        this.logger = logger;
        this.monitor = monitor;
        session.AddMonitor(monitor);
        monitor.OnRecordChanged += OnRecordChanged;
    }

    /// <summary>
    /// Handles a change event raised by <see cref="monitor"/> by forwarding it to <see cref="Raise(Record{TObject})"/>.
    /// </summary>
    /// <param name="sender">The event source (unused).</param>
    /// <param name="e">The record change event.</param>
    void OnRecordChanged(object? sender, Record<TObject> e)
    {
        Raise(e);
    }

    /// <summary>
    /// Registers a connection as subscribed to change notifications, capturing the shared broadcast
    /// group on first use. Called by each connecting hub instance; the same underlying group is
    /// supplied every time, but only needs to be captured once.
    /// </summary>
    /// <param name="g">The broadcast group for the connecting hub.</param>
    /// <param name="contextId">The connecting hub's context identifier.</param>
    public void RegisterGroup(IGroup<IRecordHubReceiver<TObject>> g, Guid contextId)
    {
        lock (gate)
        {
            logger?.LogInformation($"Registering : {contextId}");
            group ??= g; // same underlying group object every time, but only need to capture it once
            customerIds[contextId] = 0;
        }
    }

    /// <summary>
    /// Removes a connection's subscription so it no longer receives change notifications.
    /// </summary>
    /// <param name="contextId">The context identifier of the connection to unregister.</param>
    public void Unregister(Guid contextId)
    {
        lock (gate)
        {
            logger?.LogInformation($"Unregistering : {contextId}");
            customerIds.Remove(contextId);
        }
    }

    /// <summary>
    /// Broadcasts a record change event to all subscribed connections, excluding any connections
    /// listed in <see cref="Record{TObject}.Connections"/> (typically the connection that caused the change).
    /// </summary>
    /// <param name="e">The record change event to broadcast.</param>
    public void Raise(Record<TObject> e)
    {
        var excludedIds = (e.Connections ?? []).Select(id => new Guid(id)).ToList();

        List<Guid> notified;
        lock (gate)
        {
            notified = customerIds.Where(id => !excludedIds.Contains(id.Key)).Select(k => k.Key).ToList();
        }

        foreach (var id in notified)
            logger?.LogInformation($"Notifying : {id}");

        group?.Except(excludedIds).OnChanged(e);
    }

    /// <summary>
    /// Unsubscribes from the monitor's change events.
    /// </summary>
    public void Dispose()
    {
        monitor.OnRecordChanged -= OnRecordChanged;
    }
}
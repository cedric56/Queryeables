using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Queryeables.Abstraction;
using System.Text.Json;

namespace Queryeables.PostgreSql;

/// <summary>
/// Reports the changes of the watched PostgreSQL tables to the change feeds of <see cref="ChangeFeedRegistry"/>, like the SQLite
/// hooks do: a trigger on each table calls <c>pg_notify</c>, and this listener, on a dedicated connection, publishes each
/// notification.
/// <para>
/// PostgreSQL sends a notification when the transaction commits, and never for a rolled back one: a subscriber that reads
/// the row again sees its new state. Changes made by any application are reported, not only those of this one.
/// </para>
/// <para>
/// When the listening connection is lost, the listener reconnects, then tells every change feed that changes may have been
/// missed (<see cref="ChangeFeedRegistry.PublishResync"/>): the caches synchronize.
/// </para>
/// </summary>
/// <remarks>
/// Registered as a hosted service by <c>UseNpgsql(..., notifications: ...)</c>. Without a host (a console application),
/// call <see cref="StartAsync"/> yourself.
/// </remarks>
public sealed class PostgresChangeListener(
    string connectionString,
    PostgresNotifications notifications,
    ChangeFeedRegistry registry,
    ILogger<PostgresChangeListener>? logger = null) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;

    /// <summary>Completes once the listener listens for the first time (after creating the triggers).</summary>
    public Task Listening => _listening.Task;

    /// <summary>Starts listening in the background; returns at once.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _loop ??= Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is null)
            return;

        await _stop.CancelAsync();
        try
        {
            await _loop.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = notifications.ReconnectDelay;
        var connectedBefore = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(ct);

                if (notifications.CreateTriggers && !connectedBefore && notifications.Tables.Count > 0)
                {
                    await using var create = new NpgsqlCommand(notifications.TriggerScript(), connection);
                    await create.ExecuteNonQueryAsync(ct);
                }

                connection.Notification += OnNotification;
                await using (var listen = new NpgsqlCommand($"LISTEN {PostgresNotifications.Channel}", connection))
                    await listen.ExecuteNonQueryAsync(ct);

                logger?.LogInformation("Listening to PostgreSQL changes of {Count} table(s).", notifications.Tables.Count);
                _listening.TrySetResult();

                // Notifications sent while the connection was down are lost: the caches synchronize.
                if (connectedBefore)
                    registry.PublishResync();
                connectedBefore = true;
                delay = notifications.ReconnectDelay;

                while (!ct.IsCancellationRequested)
                    await connection.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "PostgreSQL change listener disconnected; reconnecting in {Delay}.", delay);
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            }
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
    {
        try
        {
            if (Parse(e.Payload) is { } change)
                registry.Publish(change.Type, change.Table, change.Key);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Could not publish the PostgreSQL change {Payload}.", e.Payload);
        }
    }

    /// <summary>Reads a notification sent by the trigger: <c>{"table": ..., "op": ..., "key": ...}</c>.</summary>
    internal static (ChangeType Type, string Table, string Key)? Parse(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("table", out var table) || !root.TryGetProperty("op", out var op))
            return null;

        var type = op.GetString() switch
        {
            "INSERT" => ChangeType.Insert,
            "UPDATE" => ChangeType.Update,
            "DELETE" => ChangeType.Delete,
            _ => ChangeType.None
        };
        var key = root.TryGetProperty("key", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return type == ChangeType.None || table.GetString() is not { } name
            ? null
            : (type, name, key ?? string.Empty);
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Queryeables.MongoDB;

/// <summary>
/// Reports the changes of the watched collections to the change feeds, through a MongoDB change stream: every insert,
/// update, replace and delete, whoever made it (the application, another process, the shell). The caches follow them.
/// <list type="bullet">
/// <item>A change stream needs a replica set (a single-node one is enough), as transactions do.</item>
/// <item>After a disconnection, the stream resumes where it stopped; when it cannot (the oplog moved on), the caches
/// synchronize instead.</item>
/// </list>
/// Registered as a hosted service by <c>UseMongoDB(..., notifications: n => n.Watch&lt;T&gt;())</c>.
/// </summary>
[RequiresUnreferencedCode(MongoAot.Trimming)]
[RequiresDynamicCode(MongoAot.DynamicCode)]
public sealed class MongoChangeListener(
    IMongoDatabase database,
    MongoNotifications notifications,
    ChangeFeedRegistry registry,
    ILogger<MongoChangeListener>? logger = null) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;

    /// <summary>Completes once the change stream is open: changes made from then on are reported.</summary>
    public Task Listening => _listening.Task;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (notifications.Collections.Count > 0)
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
        var openedBefore = false;
        BsonDocument? resumeToken = null;

        var pipeline = new EmptyPipelineDefinition<ChangeStreamDocument<BsonDocument>>()
            .Match(new BsonDocument("ns.coll", new BsonDocument("$in", new BsonArray(notifications.Collections))));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var options = new ChangeStreamOptions { ResumeAfter = resumeToken };
                using var cursor = await database.WatchAsync(pipeline, options, ct);

                logger?.LogInformation("Watching MongoDB changes of {Count} collection(s).", notifications.Collections.Count);
                _listening.TrySetResult();

                // Reopened without a place to resume from: changes made meanwhile are lost, the caches synchronize.
                if (openedBefore && resumeToken is null)
                    registry.PublishResync();
                openedBefore = true;
                delay = notifications.ReconnectDelay;

                while (await cursor.MoveNextAsync(ct))
                {
                    foreach (var change in cursor.Current)
                        Publish(change);
                    resumeToken = cursor.GetResumeToken();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (MongoCommandException ex) when (resumeToken is not null && ex.CodeName is "ChangeStreamHistoryLost" or "ChangeStreamFatalError")
            {
                logger?.LogWarning(ex, "The MongoDB change stream cannot resume; the caches synchronize.");
                resumeToken = null;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "MongoDB change stream interrupted; opening it again in {Delay}.", delay);
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

    private void Publish(ChangeStreamDocument<BsonDocument> change)
    {
        try
        {
            if (Read(change.OperationType, change.CollectionNamespace?.CollectionName, change.DocumentKey) is { } read)
                registry.Publish(read.Type, read.Table, read.Key);
            else if (change.OperationType is ChangeStreamOperationType.Invalidate or ChangeStreamOperationType.Drop
                     or ChangeStreamOperationType.Rename or ChangeStreamOperationType.DropDatabase)
                registry.PublishResync();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Could not publish the MongoDB change {Change}.", change.OperationType);
        }
    }

    /// <summary>A change of a document, as the change feeds take it; null for anything else.</summary>
    internal static (ChangeType Type, string Table, string Key)? Read(ChangeStreamOperationType operation, string? collection, BsonDocument? documentKey)
    {
        var type = operation switch
        {
            ChangeStreamOperationType.Insert => ChangeType.Insert,
            ChangeStreamOperationType.Update or ChangeStreamOperationType.Replace => ChangeType.Update,
            ChangeStreamOperationType.Delete => ChangeType.Delete,
            _ => ChangeType.None,
        };
        if (type == ChangeType.None || string.IsNullOrEmpty(collection))
            return null;
        return (type, collection, documentKey is not null && documentKey.TryGetValue("_id", out var id) ? KeyText(id) : string.Empty);
    }

    /// <summary>The key as <see cref="EntityKey.Format"/> writes it, so that the caches parse it back.</summary>
    internal static string KeyText(BsonValue id) => id switch
    {
        BsonInt64 number => EntityKey.Format(number.Value),
        BsonInt32 number => EntityKey.Format(number.Value),
        BsonString text => text.Value ?? string.Empty,
        BsonObjectId objectId => objectId.Value.ToString() ?? string.Empty,
        BsonBinaryData { SubType: BsonBinarySubType.UuidStandard } binary => EntityKey.Format(GuidConverter.FromBytes(binary.Bytes, GuidRepresentation.Standard)),
        _ => Convert.ToString(BsonTypeMapper.MapToDotNetValue(id), CultureInfo.InvariantCulture) ?? string.Empty,
    };
}

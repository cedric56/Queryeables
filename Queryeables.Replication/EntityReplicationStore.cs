using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text.Json;
using Queryeables.Abstraction;
using Routeables.Remote;

namespace Queryeables.Replication;

/// <summary>One entry of the log: a change admitted, at its position.</summary>
public sealed class ReplicationLogRow
{
    public long Id { get; set; }

    /// <summary>The position in the log: 1, 2, 3... without gap (an entry compacted leaves the table).</summary>
    public long Position { get; set; }

    /// <summary>The collections the change touches, as <c>|Person|Note|</c>: what a pull filters on.</summary>
    public string Collections { get; set; } = "";

    /// <summary>The change, as the JSON of the protocol.</summary>
    public string Change { get; set; } = "";
}

/// <summary>The merged state of one document.</summary>
public sealed class ReplicationDocumentRow
{
    public long Id { get; set; }

    public string Collection { get; set; } = "";

    public string Key { get; set; } = "";

    /// <summary>The state, as the JSON of a snapshot of the protocol (fields, stamps, tags, text).</summary>
    public string State { get; set; } = "";
}

/// <summary>A replica the server knows: its owner, the last change it decided on, its last answers.</summary>
public sealed class ReplicationReplicaRow
{
    public long Id { get; set; }

    public string Replica { get; set; } = "";

    public string User { get; set; } = "";

    public long LastSeq { get; set; }

    /// <summary>The last answers, as the JSON of a push result.</summary>
    public string Acks { get; set; } = "";
}

/// <summary>The head of the log: its last position and its horizon. One row.</summary>
public sealed class ReplicationHeadRow
{
    public long Id { get; set; }

    public long Position { get; set; }

    public long Horizon { get; set; }
}

/// <summary>
/// Writes, in the transaction of a decision, the merged documents of a collection to the application's own entities: the
/// read model that queries, reports and other services use, kept by the server alone.
/// </summary>
public interface IReplicationProjection
{
    string Collection { get; }

    Task ApplyAsync(IEntityStore store, DocumentState document, CancellationToken ct);
}

/// <summary>
/// The documents of <see cref="Collection"/> as entities <typeparamref name="TEntity"/>. <paramref name="byKey"/> finds the
/// entity of a document by its <see cref="DocumentId.Key"/>; <paramref name="map"/> writes a document that exists into its
/// entity: the one found (to update), or a new one it creates (to insert). A document deleted deletes its entity.
/// </summary>
public sealed class ReplicationProjection<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
    string collection,
    Func<string, Expression<Func<TEntity, bool>>> byKey,
    Func<DocumentState, TEntity?, TEntity> map) : IReplicationProjection where TEntity : class
{
    public string Collection { get; } = collection ?? throw new ArgumentNullException(nameof(collection));

    public async Task ApplyAsync(IEntityStore store, DocumentState document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(document);
        var existing = await store.FirstOrDefaultAsync(byKey(document.Id.Key), ct).ConfigureAwait(false);
        if (!document.Exists)
        {
            if (existing is not null)
                await store.DeleteAsync(existing, ct).ConfigureAwait(false);
            return;
        }
        var entity = map(document, existing);
        if (existing is null)
            await store.InsertAsync(entity, ct).ConfigureAwait(false);
        else
            await store.UpdateAsync(entity, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// The log, the documents and the replicas of a <see cref="ReplicationServer"/> in a Queryeables store: EF Core, MongoDB,
/// Dommel, PostgreSQL... The four row types (<see cref="ReplicationLogRow"/>, <see cref="ReplicationDocumentRow"/>,
/// <see cref="ReplicationReplicaRow"/>, <see cref="ReplicationHeadRow"/>) are entities of that store: mapped in the
/// DbContext, tables for Dommel, collections for MongoDB.
/// </summary>
/// <remarks>
/// Each decision is one transaction of the store: the entry, the documents it changed, the replica, the head, and the
/// projections. The store must support transactions (EF Core, Dommel, PostgreSQL; MongoDB as a replica set).
/// </remarks>
public sealed class EntityReplicationStore : IReplicationStore
{
    private readonly IEntityStoreFactory _stores;
    private readonly IReadOnlyList<IReplicationProjection> _projections;

    public EntityReplicationStore(IEntityStoreFactory stores, IEnumerable<IReplicationProjection>? projections = null)
    {
        _stores = stores ?? throw new ArgumentNullException(nameof(stores));
        _projections = [.. projections ?? []];
    }

    private static string CollectionsOf(Change change)
    {
        var collections = change.Ops.Select(o => o.Document.Collection).Distinct(StringComparer.Ordinal).ToList();
        if (collections.Any(c => c.Contains('|', StringComparison.Ordinal)))
            throw new ArgumentException("A collection name cannot contain '|'.", nameof(change));
        return "|" + string.Join('|', collections) + "|";
    }

    private static async Task<ReplicationHeadRow?> HeadRowAsync(IEntityStore store, CancellationToken ct)
        => await store.FirstOrDefaultAsync<ReplicationHeadRow>(_ => true, ct).ConfigureAwait(false);

    public async ValueTask<ReplicationHead> HeadAsync(CancellationToken ct = default)
    {
        await using var store = _stores.Create();
        var head = await HeadRowAsync(store, ct).ConfigureAwait(false);
        return head is null ? default : new ReplicationHead(head.Position, head.Horizon);
    }

    public async ValueTask<ReplicaRecord?> FindReplicaAsync(ReplicaId replica, CancellationToken ct = default)
    {
        await using var store = _stores.Create();
        var text = replica.ToString();
        var row = await store.FirstOrDefaultAsync<ReplicationReplicaRow>(r => r.Replica == text, ct).ConfigureAwait(false);
        return row is null ? null : new ReplicaRecord(replica, row.User, row.LastSeq, Acks(row.Acks));
    }

    private static IReadOnlyList<ChangeAck> Acks(string json)
        => string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize(json, RemoteJsonContext.Default.PushResult)!.Acks;

    public async ValueTask<DocumentState?> FindAsync(DocumentId id, CancellationToken ct = default)
    {
        await using var store = _stores.Create();
        var row = await FindRowAsync(store, id, ct).ConfigureAwait(false);
        return row is null ? null : State(row);
    }

    private static async Task<ReplicationDocumentRow?> FindRowAsync(IEntityStore store, DocumentId id, CancellationToken ct)
    {
        string collection = id.Collection, key = id.Key;
        return await store.FirstOrDefaultAsync<ReplicationDocumentRow>(d => d.Collection == collection && d.Key == key, ct).ConfigureAwait(false);
    }

    private static DocumentState State(ReplicationDocumentRow row)
        => DocumentState.FromSnapshot(JsonSerializer.Deserialize(row.State, RemoteJsonContext.Default.DocumentSnapshot)!);

    public async ValueTask<IReadOnlyList<DocumentState>> DocumentsAsync(IReadOnlyCollection<string> collections, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collections);
        if (collections.Count == 0)
            return [];
        await using var store = _stores.Create();
        var rows = await store.SelectAsync(AnyOf<ReplicationDocumentRow>(collections, c => d => d.Collection == c), ct).ConfigureAwait(false);
        return [.. rows.Select(State)];
    }

    public async ValueTask<IReadOnlyList<LogEntry>> ReadAsync(long after, long upTo, IReadOnlyCollection<string> collections, int max,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collections);
        if (collections.Count == 0 || upTo <= after)
            return [];
        await using var store = _stores.Create();
        var touches = AnyOf<ReplicationLogRow>(collections, c =>
        {
            var marker = "|" + c + "|";
            return r => r.Collections.Contains(marker);
        });
        Expression<Func<ReplicationLogRow, bool>> range = r => r.Position > after && r.Position <= upTo;
        var predicate = Expression.Lambda<Func<ReplicationLogRow, bool>>(
            Expression.AndAlso(range.Body, new Rebind(touches.Parameters[0], range.Parameters[0]).Visit(touches.Body)), range.Parameters);
        var rows = await store.SelectPagedAsync(predicate, 1, max, Sort<ReplicationLogRow>.By(r => r.Position), ct).ConfigureAwait(false);
        return [.. rows.Select(r => new LogEntry(r.Position, JsonSerializer.Deserialize(r.Change, RemoteJsonContext.Default.Change)!))];
    }

    // c1 || c2 || ...: an expression every store translates (no list captured).
    private static Expression<Func<T, bool>> AnyOf<T>(IEnumerable<string> values, Func<string, Expression<Func<T, bool>>> one)
    {
        var parameter = Expression.Parameter(typeof(T), "r");
        Expression? body = null;
        foreach (var value in values.Distinct(StringComparer.Ordinal))
        {
            var each = one(value);
            var rebound = new Rebind(each.Parameters[0], parameter).Visit(each.Body);
            body = body is null ? rebound : Expression.OrElse(body, rebound);
        }
        return Expression.Lambda<Func<T, bool>>(body ?? Expression.Constant(false), parameter);
    }

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    public async ValueTask CommitAsync(ReplicationCommit commit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await using var store = _stores.Create();
        await using var transaction = await store.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (commit.Entry is { } entry)
        {
            var head = await HeadRowAsync(store, ct).ConfigureAwait(false);
            var position = head?.Position ?? 0;
            if (entry.Position != position + 1)
                throw new InvalidOperationException($"The next position is {position + 1}, not {entry.Position}: another server wrote to this log.");
            await store.InsertAsync(new ReplicationLogRow
            {
                Position = entry.Position,
                Collections = CollectionsOf(entry.Change),
                Change = JsonSerializer.Serialize(entry.Change, RemoteJsonContext.Default.Change),
            }, ct).ConfigureAwait(false);
            if (head is null)
                await store.InsertAsync(new ReplicationHeadRow { Position = entry.Position }, ct).ConfigureAwait(false);
            else
            {
                head.Position = entry.Position;
                await store.UpdateAsync(head, ct).ConfigureAwait(false);
            }
        }

        foreach (var document in commit.Documents)
        {
            var state = JsonSerializer.Serialize(document.ToSnapshot(), RemoteJsonContext.Default.DocumentSnapshot);
            var row = await FindRowAsync(store, document.Id, ct).ConfigureAwait(false);
            if (row is null)
                await store.InsertAsync(new ReplicationDocumentRow { Collection = document.Id.Collection, Key = document.Id.Key, State = state }, ct).ConfigureAwait(false);
            else
            {
                row.State = state;
                await store.UpdateAsync(row, ct).ConfigureAwait(false);
            }
            foreach (var projection in _projections)
                if (projection.Collection == document.Id.Collection)
                    await projection.ApplyAsync(store, document, ct).ConfigureAwait(false);
        }

        var replica = commit.Replica.Replica.ToString();
        var acks = JsonSerializer.Serialize(new PushResult(commit.Replica.Acks), RemoteJsonContext.Default.PushResult);
        var record = await store.FirstOrDefaultAsync<ReplicationReplicaRow>(r => r.Replica == replica, ct).ConfigureAwait(false);
        if (record is null)
            await store.InsertAsync(new ReplicationReplicaRow { Replica = replica, User = commit.Replica.User, LastSeq = commit.Replica.LastSeq, Acks = acks }, ct).ConfigureAwait(false);
        else
        {
            record.User = commit.Replica.User;
            record.LastSeq = commit.Replica.LastSeq;
            record.Acks = acks;
            await store.UpdateAsync(record, ct).ConfigureAwait(false);
        }

        var result = await transaction.CommitAsync(ct).ConfigureAwait(false);
        if (!result.Successful)
            throw new EntityStoreException(result.ErrorCode, result.Exception ?? "The decision could not be stored.", result.Errors);
    }

    public async ValueTask CompactAsync(long upTo, CancellationToken ct = default)
    {
        await using var store = _stores.Create();
        await using var transaction = await store.BeginTransactionAsync(ct).ConfigureAwait(false);
        var head = await HeadRowAsync(store, ct).ConfigureAwait(false);
        if (head is null)
            return;
        upTo = Math.Min(upTo, head.Position);
        foreach (var row in await store.SelectAsync<ReplicationLogRow>(r => r.Position <= upTo, ct).ConfigureAwait(false))
            await store.DeleteAsync(row, ct).ConfigureAwait(false);
        head.Horizon = Math.Max(head.Horizon, upTo);
        await store.UpdateAsync(head, ct).ConfigureAwait(false);
        var result = await transaction.CommitAsync(ct).ConfigureAwait(false);
        if (!result.Successful)
            throw new EntityStoreException(result.ErrorCode, result.Exception ?? "The log could not be compacted.", result.Errors);
    }
}

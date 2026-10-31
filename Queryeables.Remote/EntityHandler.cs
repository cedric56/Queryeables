using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables.Remote;

/// <summary>
/// Handles, server side, the requests for one entity type through <see cref="IEntityStore"/>: it therefore works with
/// Dommel as well as EF. Each call goes through the access policy, then validation for writes.
/// Errors are returned in the <see cref="RemoteResult"/>, with their kind. Entities are read and written with the
/// <see cref="IEntitySerializer"/> of the server, given by <see cref="EntityRequestDispatcher"/>.
/// </summary>
public abstract class EntityHandler
{
    /// <summary>The default limit of a read that is not paged.</summary>
    public const int DefaultMaxReadSize = 10_000;

    public abstract Type Type { get; }

    public abstract Task<RemoteResult> GetAllAsync(IEntitySerializer serializer, EntityRequestContext context, CancellationToken ct = default);

    public abstract Task<RemoteResult> SelectAsync(IEntitySerializer serializer, FilterCondition[] conditions, bool any,
        EntityRequestContext context, CancellationToken ct = default);

    public abstract Task<RemoteResult> SelectPagedAsync(IEntitySerializer serializer, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        EntityRequestContext context, CancellationToken ct = default);

    /// <summary>A page in the given order. The properties of the sort are checked against the entity type.</summary>
    public virtual Task<RemoteResult> SelectPagedAsync(IEntitySerializer serializer, FilterCondition[] conditions, bool any, int pageNumber,
        int pageSize, SortKey[] sort, EntityRequestContext context, CancellationToken ct = default)
        => sort is not { Length: > 0 }
            ? SelectPagedAsync(serializer, conditions, any, pageNumber, pageSize, context, ct)
            : Task.FromResult(RemoteResult.Failure(EntityErrorCode.NotSupported, $"{Type.Name} does not support sorted pages."));

    public abstract Task<RemoteResult> GetChangesAsync(IEntitySerializer serializer, DateTime sinceUtc, EntityRequestContext context, CancellationToken ct = default);

    /// <summary>Standalone write, in its own store.</summary>
    public abstract Task<RemoteResult> WriteAsync(IEntitySerializer serializer, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, CancellationToken ct = default);

    /// <summary>
    /// Write in a given store, for example during the transaction of a batch.
    /// Throws <see cref="EntityStoreException"/> on failure, which rolls back the batch.
    /// </summary>
    /// <param name="requireEffect">When true, an update or delete with no effect is an error (NotFound).</param>
    public abstract Task<byte[]> WriteAsync(IEntitySerializer serializer, IEntityStore store, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, bool requireEffect, CancellationToken ct = default);
}

/// <example>
/// <code>
/// registry.Register(new EntityHandler&lt;Customer&gt;(stores, EntityAccessPolicies.Authenticated));
/// </code>
/// </example>
/// <param name="storeFactory">Creates a store per call; the handler disposes it.</param>
/// <param name="policy">Access policy, mandatory. <see cref="EntityAccessPolicies.AllowAll"/> to allow everything.</param>
/// <param name="validate">Validates entities (DataAnnotations) before insert and update. True by default.</param>
/// <param name="maxPageSize">
/// Maximum page size: a larger page is reduced to this size (idea taken from Datasync, see CREDITS.md).
/// </param>
/// <param name="rows">The rows each user may reach (see <see cref="EntityRowFilter{T}"/>); every row when null.</param>
/// <param name="maxReadSize">
/// Most rows a read that is not paged may return (every row, a filter, the changes since a date). Beyond it, the read
/// fails with <see cref="EntityErrorCode.InvalidRequest"/> instead of sending the table: the client pages, or narrows its
/// filter.
/// </param>
/// <param name="aggregate">
/// When provided, standalone writes go through this service instead of the store: insert and update call
/// <see cref="IDbTransaction{TEntity}.SaveAsync"/>, delete calls <see cref="IDbTransaction{TEntity}.RemoveAsync"/>.
/// Use it to save an entity together with its children (a person and its contacts) in one transaction.
/// Access policy and validation still apply. In a batch, a graph service (<see cref="IEntityGraphService{TEntity}"/>) runs
/// its graph in the batch's transaction; any other service opens its own transaction, so its type cannot be part of a batch.
/// </param>
public sealed class EntityHandler<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(
    Func<IEntityStore> storeFactory,
    IEntityAccessPolicy policy,
    bool validate = true,
    int maxPageSize = 1000,
    IDbTransaction<T>? aggregate = null,
    EntityRowFilter<T>? rows = null,
    int maxReadSize = EntityHandler.DefaultMaxReadSize) : EntityHandler where T : class
{
    /// <inheritdoc cref="EntityHandler{T}"/>
    public EntityHandler(IEntityStoreFactory stores, IEntityAccessPolicy policy, bool validate = true, int maxPageSize = 1000,
        IDbTransaction<T>? aggregate = null, EntityRowFilter<T>? rows = null, int maxReadSize = DefaultMaxReadSize)
        : this((stores ?? throw new ArgumentNullException(nameof(stores))).Create, policy, validate, maxPageSize, aggregate, rows,
            maxReadSize)
    {
    }

    private readonly Func<IEntityStore> _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
    private readonly IEntityAccessPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly int _maxPageSize = maxPageSize > 0 ? maxPageSize : throw new ArgumentOutOfRangeException(nameof(maxPageSize));
    private readonly int _maxReadSize = maxReadSize > 0 ? maxReadSize : throw new ArgumentOutOfRangeException(nameof(maxReadSize));
    private static readonly Expression<Func<T, bool>> All = _ => true;

    public override Type Type => typeof(T);

    // ---------- Read ----------

    public override Task<RemoteResult> GetAllAsync(IEntitySerializer serializer, EntityRequestContext context, CancellationToken ct = default)
        => RemoteResult.RunAsync(async () =>
        {
            await AuthorizeAsync(EntityOperation.Read, null, context, ct);
            await using var store = _storeFactory();
            return SerializeMany(serializer, await ReadBoundedAsync(store, Restrict(All, context), ct));
        });

    public override Task<RemoteResult> SelectAsync(IEntitySerializer serializer, FilterCondition[] conditions, bool any,
        EntityRequestContext context, CancellationToken ct = default)
        => (conditions?.Length ?? 0) == 0
            ? GetAllAsync(serializer, context, ct)
            : RemoteResult.RunAsync(async () =>
            {
                var predicate = Filters.ToPredicate<T>(conditions!, any);
                await AuthorizeAsync(EntityOperation.Read, null, context, ct);
                await using var store = _storeFactory();
                return SerializeMany(serializer, await ReadBoundedAsync(store, Restrict(predicate, context), ct));
            });

    public override Task<RemoteResult> SelectPagedAsync(IEntitySerializer serializer, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        EntityRequestContext context, CancellationToken ct = default)
        => RemoteResult.RunAsync(async () =>
        {
            var predicate = Filters.ToPredicate<T>(conditions ?? [], any);
            await AuthorizeAsync(EntityOperation.Read, null, context, ct);
            await using var store = _storeFactory();
            return SerializeMany(serializer,
                await store.SelectPagedAsync(Restrict(predicate, context), pageNumber, Math.Min(pageSize, _maxPageSize), ct));
        });

    public override Task<RemoteResult> SelectPagedAsync(IEntitySerializer serializer, FilterCondition[] conditions, bool any, int pageNumber,
        int pageSize, SortKey[] sort, EntityRequestContext context, CancellationToken ct = default)
        => sort is not { Length: > 0 }
            ? SelectPagedAsync(serializer, conditions, any, pageNumber, pageSize, context, ct)
            : RemoteResult.RunAsync(async () =>
            {
                var predicate = Filters.ToPredicate<T>(conditions ?? [], any);
                var order = Sort<T>.FromKeys(sort);       // only properties of T, at most Sort<T>.MaxKeys
                await AuthorizeAsync(EntityOperation.Read, null, context, ct);
                await using var store = _storeFactory();
                return SerializeMany(serializer,
                    await store.SelectPagedAsync(Restrict(predicate, context), pageNumber, Math.Min(pageSize, _maxPageSize), order, ct));
            });

    public override Task<RemoteResult> GetChangesAsync(IEntitySerializer serializer, DateTime sinceUtc, EntityRequestContext context, CancellationToken ct = default)
        => RemoteResult.RunAsync(async () =>
        {
            await AuthorizeAsync(EntityOperation.Read, null, context, ct);
            await using var store = _storeFactory();

            // Counted first, so that a date far in the past does not read the whole table. The count leaves out the
            // soft-deleted rows, which the changes include: the result is checked again once read.
            if (await store.CountAsync(Restrict(SyncMetadata.ModifiedSince<T>(sinceUtc), context), ct) > _maxReadSize)
                throw TooMany();

            var changes = await store.SelectChangesAsync<T>(sinceUtc, ct);
            if (rows is not null)
            {
                var visible = rows.Rows(context.User).Compile();
                changes = changes.Where(visible).ToList();
            }
            return changes.Count > _maxReadSize ? throw TooMany() : SerializeMany(serializer, changes);
        });

    // ---------- Write ----------

    public override Task<RemoteResult> WriteAsync(IEntitySerializer serializer, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, CancellationToken ct = default)
        => RemoteResult.RunAsync(async () =>
        {
            if (aggregate is not null)
                return await WriteThroughAggregateAsync(serializer, aggregate, kind, entity, context, ct);

            await using var store = _storeFactory();
            return await WriteAsync(serializer, store, kind, entity, context, requireEffect: false, ct);
        });

    public override async Task<byte[]> WriteAsync(IEntitySerializer serializer, IEntityStore store, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, bool requireEffect, CancellationToken ct = default)
    {
        if (aggregate is not null and not IEntityGraphService<T>)
            throw new EntityStoreException(EntityErrorCode.NotSupported,
                $"{typeof(T).Name} is saved by its own transactional service: it cannot be part of a batch.");

        var value = await PrepareAsync(serializer, kind, entity, context, ct);
        await CheckRowsAsync(store, kind, value, context, ct);

        // A graph: the parent and its children, in the batch's transaction.
        if (aggregate is IEntityGraphService<T> { Graph: var graph })
        {
            using (ChangeContext.Begin(context.ConnectionId))
            {
                if (kind == BatchOperationKind.Delete)
                {
                    await graph.RemoveAsync(store, value, ct);
                    return serializer.Serialize(true);
                }

                await graph.SaveAsync(store, value, ct);
                return serializer.Serialize(value); // generated keys, also those of the children
            }
        }

        // The client that initiated the change is passed on to the notification source.
        using (ChangeContext.Begin(context.ConnectionId))
        {
            switch (kind)
            {
                case BatchOperationKind.Insert:
                    await store.InsertAsync(value, ct);
                    return serializer.Serialize(value); // generated key and version

                case BatchOperationKind.Update:
                    if (await store.UpdateAsync(value, ct))
                        return serializer.Serialize(value); // new version
                    return requireEffect ? throw NotFound() : [];

                default:
                    var deleted = await store.DeleteAsync(value, ct);
                    if (!deleted && requireEffect)
                        throw NotFound();
                    return serializer.Serialize(deleted);
            }
        }
    }

    private async Task<byte[]> WriteThroughAggregateAsync(IEntitySerializer serializer, IDbTransaction<T> service, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, CancellationToken ct)
    {
        var value = await PrepareAsync(serializer, kind, entity, context, ct);
        if (rows is not null)
        {
            await using var store = _storeFactory();
            await CheckRowsAsync(store, kind, value, context, ct);
        }

        using (ChangeContext.Begin(context.ConnectionId))
        {
            if (kind == BatchOperationKind.Delete)
            {
                (await service.RemoveAsync(value, ct)).ThrowIfFailed();
                return serializer.Serialize(true);
            }

            (await service.SaveAsync(value, ct)).ThrowIfFailed();
            return serializer.Serialize(value); // generated keys, also those of the children
        }
    }

    /// <summary>Deserializes the entity, checks the access policy and validates it for insert and update.</summary>
    private async Task<T> PrepareAsync(IEntitySerializer serializer, BatchOperationKind kind, byte[] entity, EntityRequestContext context, CancellationToken ct)
    {
        var value = serializer.Deserialize<T>(entity) ?? throw new ArgumentException($"Empty {typeof(T).Name} entity.");

        var operation = kind switch
        {
            BatchOperationKind.Insert => EntityOperation.Insert,
            BatchOperationKind.Update => EntityOperation.Update,
            BatchOperationKind.Delete => EntityOperation.Delete,
            _ => throw new ArgumentException($"Unknown operation: {kind}.")
        };
        await AuthorizeAsync(operation, value, context, ct);

        if (validate && kind is BatchOperationKind.Insert or BatchOperationKind.Update)
        {
            var errors = EntityValidator.Validate(value).Select(e => e.ErrorMessage ?? "Invalid value.").ToList();
            if (errors.Count > 0)
                throw new EntityStoreException(EntityErrorCode.Validation,
                    $"Invalid {typeof(T).Name}: {string.Join(" ", errors)}", errors);
        }

        return value;
    }

    // ---------- Internals ----------

    private Expression<Func<T, bool>> Restrict(Expression<Func<T, bool>> predicate, EntityRequestContext context)
        => rows is null ? predicate : rows.Restrict(predicate, context.User);

    /// <summary>At most <c>maxReadSize</c> rows, read in one query: one more is enough to know there are too many.</summary>
    private async Task<IReadOnlyList<T>> ReadBoundedAsync(IEntityStore store, Expression<Func<T, bool>> predicate, CancellationToken ct)
    {
        var found = await store.SelectPagedAsync(predicate, 1, _maxReadSize + 1, ct);
        return found.Count > _maxReadSize ? throw TooMany() : found;
    }

    private EntityStoreException TooMany()
        => new(EntityErrorCode.InvalidRequest,
            $"More than {_maxReadSize} {typeof(T).Name} rows match: read them by page, or narrow the filter.");

    /// <summary>
    /// With a row filter: the written entity must be one of the user's rows, and so must the stored row an update or a
    /// delete targets. A row of someone else answers NotFound, as if it did not exist.
    /// </summary>
    private async Task CheckRowsAsync(IEntityStore store, BatchOperationKind kind, T value, EntityRequestContext context, CancellationToken ct)
    {
        if (rows is null)
            return;

        // The stored row first: someone else's row answers NotFound, whatever the new content says.
        var visible = rows.Rows(context.User);
        if (kind != BatchOperationKind.Insert
            && await store.FirstOrDefaultAsync(EntityRowFilter<T>.And(rows.SameKey(value), visible), ct) is null)
            throw NotFound();

        if (kind != BatchOperationKind.Delete && !visible.Compile()(value))
            throw new EntityStoreException(EntityErrorCode.Forbidden,
                $"Access denied: this {typeof(T).Name} is outside the rows you may write.");
    }

    private async Task AuthorizeAsync(EntityOperation operation, T? entity, EntityRequestContext context, CancellationToken ct)
    {
        if (!await _policy.IsAllowedAsync(new EntityAccessRequest(operation, typeof(T), entity, context.User), ct))
            throw new EntityStoreException(EntityErrorCode.Forbidden, $"Access denied: {operation} on {typeof(T).Name}.");
    }

    private static EntityStoreException NotFound()
        => new(EntityErrorCode.NotFound, $"{typeof(T).Name} not found: the row no longer exists.");

    private static byte[] SerializeMany(IEntitySerializer serializer, IReadOnlyList<T> entities)
        => serializer.Serialize(entities.ToArray());
}

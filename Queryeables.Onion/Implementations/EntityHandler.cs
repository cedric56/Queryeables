using MessagePack;
using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Onion;

/// <summary>
/// Handles, server side, the requests for one entity type through <see cref="IEntityStore"/>: it therefore works with
/// Dommel as well as EF. Each call goes through the access policy, then validation for writes.
/// Errors are returned in the <see cref="DBResultMessage"/>, with their kind.
/// </summary>
public abstract class EntityHandler
{
    public abstract Type Type { get; }

    public abstract Task<DBResultMessage> GetAllAsync(EntityRequestContext context, CancellationToken ct = default);

    public abstract Task<DBResultMessage> SelectAsync(FilterCondition[] conditions, bool any,
        EntityRequestContext context, CancellationToken ct = default);

    public abstract Task<DBResultMessage> SelectPagedAsync(FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        EntityRequestContext context, CancellationToken ct = default);

    public abstract Task<DBResultMessage> GetChangesAsync(DateTime sinceUtc, EntityRequestContext context, CancellationToken ct = default);

    /// <summary>Standalone write, in its own store.</summary>
    public abstract Task<DBResultMessage> WriteAsync(BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, CancellationToken ct = default);

    /// <summary>
    /// Write in a given store, for example during the transaction of a batch.
    /// Throws <see cref="EntityStoreException"/> on failure, which rolls back the batch.
    /// </summary>
    /// <param name="requireEffect">When true, an update or delete with no effect is an error (NotFound).</param>
    public abstract Task<byte[]> WriteAsync(IEntityStore store, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, bool requireEffect, CancellationToken ct = default);
}

/// <example>
/// <code>
/// registry.Register(new EntityHandler&lt;Customer&gt;(
///     () => DommelEntityStore.Create(factory),
///     EntityAccessPolicies.Authenticated));
/// </code>
/// </example>
/// <param name="storeFactory">Creates a store per call; the handler disposes it.</param>
/// <param name="policy">Access policy, mandatory. <see cref="EntityAccessPolicies.AllowAll"/> to allow everything.</param>
/// <param name="validate">Validates entities (DataAnnotations) before insert and update. True by default.</param>
/// <param name="maxPageSize">
/// Maximum page size: a larger page is reduced to this size (idea taken from Datasync, see CREDITS.md).
/// </param>
/// <param name="aggregate">
/// When provided, standalone writes go through this service instead of the store: insert and update call
/// <see cref="IDbTransaction{TEntity}.SaveAsync"/>, delete calls <see cref="IDbTransaction{TEntity}.RemoveAsync"/>.
/// Use it to save an entity together with its children (a person and its contacts) in one transaction.
/// Access policy and validation still apply. Such a type cannot be part of a batch, since the service opens its
/// own transaction.
/// </param>
public sealed class EntityHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
    Func<IEntityStore> storeFactory,
    IEntityAccessPolicy policy,
    MessagePackSerializerOptions? options = null,
    bool validate = true,
    int maxPageSize = 1000,
    IDbTransaction<T>? aggregate = null) : EntityHandler where T : class
{
    private readonly Func<IEntityStore> _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
    private readonly IEntityAccessPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly int _maxPageSize = maxPageSize > 0 ? maxPageSize : throw new ArgumentOutOfRangeException(nameof(maxPageSize));

    public override Type Type => typeof(T);

    // ---------- Read ----------

    public override Task<DBResultMessage> GetAllAsync(EntityRequestContext context, CancellationToken ct = default)
        => DBResultMessage.RunAsync(async () =>
        {
            await AuthorizeAsync(EntityOperation.Read, null, context, ct);
            await using var store = _storeFactory();
            return SerializeMany(await store.GetAllAsync<T>(ct), ct);
        });

    public override Task<DBResultMessage> SelectAsync(FilterCondition[] conditions, bool any,
        EntityRequestContext context, CancellationToken ct = default)
        => (conditions?.Length ?? 0) == 0
            ? GetAllAsync(context, ct)
            : DBResultMessage.RunAsync(async () =>
            {
                var predicate = Filters.ToPredicate<T>(conditions!, any);
                await AuthorizeAsync(EntityOperation.Read, null, context, ct);
                await using var store = _storeFactory();
                return SerializeMany(await store.SelectAsync(predicate, ct), ct);
            });

    public override Task<DBResultMessage> SelectPagedAsync(FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        EntityRequestContext context, CancellationToken ct = default)
        => DBResultMessage.RunAsync(async () =>
        {
            var predicate = Filters.ToPredicate<T>(conditions ?? [], any);
            await AuthorizeAsync(EntityOperation.Read, null, context, ct);
            await using var store = _storeFactory();
            return SerializeMany(await store.SelectPagedAsync(predicate, pageNumber, Math.Min(pageSize, _maxPageSize), ct), ct);
        });

    public override Task<DBResultMessage> GetChangesAsync(DateTime sinceUtc, EntityRequestContext context, CancellationToken ct = default)
        => DBResultMessage.RunAsync(async () =>
        {
            await AuthorizeAsync(EntityOperation.Read, null, context, ct);
            await using var store = _storeFactory();
            return SerializeMany(await store.SelectChangesAsync<T>(sinceUtc, ct), ct);
        });

    // ---------- Write ----------

    public override Task<DBResultMessage> WriteAsync(BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, CancellationToken ct = default)
        => DBResultMessage.RunAsync(async () =>
        {
            if (aggregate is not null)
                return await WriteThroughAggregateAsync(aggregate, kind, entity, context, ct);

            await using var store = _storeFactory();
            return await WriteAsync(store, kind, entity, context, requireEffect: false, ct);
        });

    public override async Task<byte[]> WriteAsync(IEntityStore store, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, bool requireEffect, CancellationToken ct = default)
    {
        if (aggregate is not null)
            throw new EntityStoreException(EntityErrorCode.NotSupported,
                $"{typeof(T).Name} is saved by its own transactional service: it cannot be part of a batch.");

        var value = await PrepareAsync(kind, entity, context, ct);

        // The client that initiated the change is passed on to the notification source.
        using (ChangeContext.Begin(context.ConnectionId))
        {
            switch (kind)
            {
                case BatchOperationKind.Insert:
                    await store.InsertAsync(value, ct);
                    return Serialize(value, ct); // generated key and version

                case BatchOperationKind.Update:
                    if (await store.UpdateAsync(value, ct))
                        return Serialize(value, ct); // new version
                    return requireEffect ? throw NotFound() : [];

                default:
                    var deleted = await store.DeleteAsync(value, ct);
                    if (!deleted && requireEffect)
                        throw NotFound();
                    return MessagePackSerializer.Serialize(deleted, options, ct);
            }
        }
    }

    private async Task<byte[]> WriteThroughAggregateAsync(IDbTransaction<T> service, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, CancellationToken ct)
    {
        var value = await PrepareAsync(kind, entity, context, ct);

        using (ChangeContext.Begin(context.ConnectionId))
        {
            if (kind == BatchOperationKind.Delete)
            {
                (await service.RemoveAsync(value, ct)).ThrowIfFailed();
                return MessagePackSerializer.Serialize(true, options, ct);
            }

            (await service.SaveAsync(value, ct)).ThrowIfFailed();
            return Serialize(value, ct); // generated keys, also those of the children
        }
    }

    /// <summary>Deserializes the entity, checks the access policy and validates it for insert and update.</summary>
    private async Task<T> PrepareAsync(BatchOperationKind kind, byte[] entity, EntityRequestContext context, CancellationToken ct)
    {
        var value = Deserialize(entity, ct);

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

    private async Task AuthorizeAsync(EntityOperation operation, T? entity, EntityRequestContext context, CancellationToken ct)
    {
        if (!await _policy.IsAllowedAsync(new EntityAccessRequest(operation, typeof(T), entity, context.User), ct))
            throw new EntityStoreException(EntityErrorCode.Forbidden, $"Access denied: {operation} on {typeof(T).Name}.");
    }

    private static EntityStoreException NotFound()
        => new(EntityErrorCode.NotFound, $"{typeof(T).Name} not found: the row no longer exists.");

    private T Deserialize(byte[] bytes, CancellationToken ct)
        => MessagePackSerializer.Deserialize<T>(bytes, options, ct)
           ?? throw new ArgumentException($"Empty {typeof(T).Name} entity.");

    private byte[] Serialize(T value, CancellationToken ct) => MessagePackSerializer.Serialize(value, options, ct);

    private byte[] SerializeMany(IReadOnlyList<T> entities, CancellationToken ct)
        => MessagePackSerializer.Serialize(entities.ToArray(), options, ct);
}

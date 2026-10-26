using MessagePack;
using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Onion;

/// <summary>
/// Handles, server side, the requests for one entity type through <see cref="IEntityStore"/>: it therefore works with
/// Dommel as well as EF. Each call goes through the access policy, then validation for writes.
/// Errors are returned in the <see cref="DBResult"/>, with their kind.
/// </summary>
public abstract class EntityHandler
{
    public abstract Type Type { get; }

    public abstract Task<DBResult> GetAllAsync(EntityRequestContext context, CancellationToken ct = default);

    public abstract Task<DBResult> SelectAsync(FilterCondition[] conditions, bool any,
        EntityRequestContext context, CancellationToken ct = default);

    public abstract Task<DBResult> SelectPagedAsync(FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        EntityRequestContext context, CancellationToken ct = default);

    public abstract Task<DBResult> GetChangesAsync(DateTime sinceUtc, EntityRequestContext context, CancellationToken ct = default);

    /// <summary>Standalone write, in its own store.</summary>
    public abstract Task<DBResult> WriteAsync(BatchOperationKind kind, byte[] entity,
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
public sealed class EntityHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
    Func<IEntityStore> storeFactory,
    IEntityAccessPolicy policy,
    MessagePackSerializerOptions? options = null,
    bool validate = true,
    int maxPageSize = 1000) : EntityHandler where T : class
{
    private readonly Func<IEntityStore> _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
    private readonly IEntityAccessPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly int _maxPageSize = maxPageSize > 0 ? maxPageSize : throw new ArgumentOutOfRangeException(nameof(maxPageSize));

    public override Type Type => typeof(T);

    // ---------- Read ----------

    public override Task<DBResult> GetAllAsync(EntityRequestContext context, CancellationToken ct = default)
        => DBResult.RunAsync(async () =>
        {
            await AuthorizeAsync(EntityOperation.Read, null, context, ct);
            await using var store = _storeFactory();
            return SerializeMany(await store.GetAllAsync<T>(ct), ct);
        });

    public override Task<DBResult> SelectAsync(FilterCondition[] conditions, bool any,
        EntityRequestContext context, CancellationToken ct = default)
        => (conditions?.Length ?? 0) == 0
            ? GetAllAsync(context, ct)
            : DBResult.RunAsync(async () =>
            {
                var predicate = Filters.ToPredicate<T>(conditions!, any);
                await AuthorizeAsync(EntityOperation.Read, null, context, ct);
                await using var store = _storeFactory();
                return SerializeMany(await store.SelectAsync(predicate, ct), ct);
            });

    public override Task<DBResult> SelectPagedAsync(FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        EntityRequestContext context, CancellationToken ct = default)
        => DBResult.RunAsync(async () =>
        {
            var predicate = Filters.ToPredicate<T>(conditions ?? [], any);
            await AuthorizeAsync(EntityOperation.Read, null, context, ct);
            await using var store = _storeFactory();
            return SerializeMany(await store.SelectPagedAsync(predicate, pageNumber, Math.Min(pageSize, _maxPageSize), ct), ct);
        });

    public override Task<DBResult> GetChangesAsync(DateTime sinceUtc, EntityRequestContext context, CancellationToken ct = default)
        => DBResult.RunAsync(async () =>
        {
            await AuthorizeAsync(EntityOperation.Read, null, context, ct);
            await using var store = _storeFactory();
            return SerializeMany(await store.SelectChangesAsync<T>(sinceUtc, ct), ct);
        });

    // ---------- Write ----------

    public override Task<DBResult> WriteAsync(BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, CancellationToken ct = default)
        => DBResult.RunAsync(async () =>
        {
            await using var store = _storeFactory();
            return await WriteAsync(store, kind, entity, context, requireEffect: false, ct);
        });

    public override async Task<byte[]> WriteAsync(IEntityStore store, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, bool requireEffect, CancellationToken ct = default)
    {
        var value = Deserialize(entity, ct);

        var operation = kind switch
        {
            BatchOperationKind.Insert => EntityOperation.Insert,
            BatchOperationKind.Update => EntityOperation.Update,
            BatchOperationKind.Delete => EntityOperation.Delete,
            _ => throw new ArgumentException($"Opération inconnue : {kind}.")
        };
        await AuthorizeAsync(operation, value, context, ct);

        if (validate && kind is BatchOperationKind.Insert or BatchOperationKind.Update)
        {
            var errors = EntityValidator.Validate(value).Select(e => e.ErrorMessage ?? "Valeur invalide.").ToList();
            if (errors.Count > 0)
                throw new EntityStoreException(EntityErrorCode.Validation,
                    $"{typeof(T).Name} invalide : {string.Join(" ", errors)}", errors);
        }

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

    // ---------- Internals ----------

    private async Task AuthorizeAsync(EntityOperation operation, T? entity, EntityRequestContext context, CancellationToken ct)
    {
        if (!await _policy.IsAllowedAsync(new EntityAccessRequest(operation, typeof(T), entity, context.User), ct))
            throw new EntityStoreException(EntityErrorCode.Forbidden, $"Accès refusé : {operation} sur {typeof(T).Name}.");
    }

    private static EntityStoreException NotFound()
        => new(EntityErrorCode.NotFound, $"{typeof(T).Name} introuvable : la ligne n'existe plus.");

    private T Deserialize(byte[] bytes, CancellationToken ct)
        => MessagePackSerializer.Deserialize<T>(bytes, options, ct)
           ?? throw new ArgumentException($"Entité {typeof(T).Name} vide.");

    private byte[] Serialize(T value, CancellationToken ct) => MessagePackSerializer.Serialize(value, options, ct);

    private byte[] SerializeMany(IReadOnlyList<T> entities, CancellationToken ct)
        => MessagePackSerializer.Serialize(entities.ToArray(), options, ct);
}

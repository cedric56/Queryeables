using MessagePack;
using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Onion;

/// <summary>
/// Traite côté serveur les requêtes d'un type d'entité, via <see cref="IEntityStore"/> : fonctionne donc avec
/// Dommel comme avec EF. Chaque appel passe par la règle d'accès, puis la validation pour les écritures.
/// Les erreurs sont renvoyées dans le <see cref="DBResult"/>, avec leur nature.
/// </summary>
public abstract class EntityHandler
{
    public abstract Type Type { get; }

    public abstract Task<DBResult> GetAllAsync(EntityRequestContext context, CancellationToken ct = default);

    public abstract Task<DBResult> SelectAsync(FilterCondition[] conditions, bool any,
        EntityRequestContext context, CancellationToken ct = default);

    public abstract Task<DBResult> SelectPagedAsync(FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        EntityRequestContext context, CancellationToken ct = default);

    /// <summary>Écriture isolée, dans son propre store.</summary>
    public abstract Task<DBResult> WriteAsync(BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, CancellationToken ct = default);

    /// <summary>
    /// Écriture dans un store fourni, par exemple pendant la transaction d'un lot.
    /// Lève <see cref="EntityStoreException"/> en cas d'échec, ce qui annule le lot.
    /// </summary>
    /// <param name="requireEffect">Si vrai, une mise à jour ou suppression sans effet est une erreur (NotFound).</param>
    public abstract Task<byte[]> WriteAsync(IEntityStore store, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, bool requireEffect, CancellationToken ct = default);
}

/// <example>
/// <code>
/// registry.Register(new EntityHandler&lt;Client&gt;(
///     () => DommelEntityStore.Create(factory),
///     EntityAccessPolicies.Authenticated));
/// </code>
/// </example>
/// <param name="storeFactory">Crée un store par appel ; le gestionnaire le dispose.</param>
/// <param name="policy">Règle d'accès, obligatoire. <see cref="EntityAccessPolicies.AllowAll"/> pour tout permettre.</param>
/// <param name="validate">Valide les entités (DataAnnotations) avant insertion et mise à jour. Vrai par défaut.</param>
public sealed class EntityHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
    Func<IEntityStore> storeFactory,
    IEntityAccessPolicy policy,
    MessagePackSerializerOptions? options = null,
    bool validate = true) : EntityHandler where T : class
{
    private readonly Func<IEntityStore> _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
    private readonly IEntityAccessPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    public override Type Type => typeof(T);

    // ---------- Lecture ----------

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
            return SerializeMany(await store.SelectPagedAsync(predicate, pageNumber, pageSize, ct), ct);
        });

    // ---------- Écriture ----------

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

        // Le client à l'origine du changement est transmis à la source de notifications.
        using (ChangeContext.Begin(context.ConnectionId))
        {
            switch (kind)
            {
                case BatchOperationKind.Insert:
                    await store.InsertAsync(value, ct);
                    return Serialize(value, ct); // clé générée et version

                case BatchOperationKind.Update:
                    if (await store.UpdateAsync(value, ct))
                        return Serialize(value, ct); // nouvelle version
                    return requireEffect ? throw NotFound() : [];

                default:
                    var deleted = await store.DeleteAsync(value, ct);
                    if (!deleted && requireEffect)
                        throw NotFound();
                    return MessagePackSerializer.Serialize(deleted, options, ct);
            }
        }
    }

    // ---------- Interne ----------

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

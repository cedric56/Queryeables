using MagicOnion;
using MessagePack;
using Queryeables.Abstraction;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Onion;

/// <summary>
/// <see cref="IEntityStore"/> côté client, au-dessus du service MagicOnion. Permet d'utiliser
/// <see cref="EntityCache{TEntity, TKey}"/> à distance comme en local.
/// <para>
/// Les prédicats doivent être des égalités propriété = valeur, toutes en ET ou toutes en OU
/// (par exemple <c>c => c.Id == id</c>) : le serveur n'exécute jamais de requête arbitraire.
/// Les transactions ne sont pas prises en charge : chaque écriture est validée côté serveur.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var layer = MagicOnionClient.Create&lt;IMagicOnionLayer&gt;(channel);
/// var cache = new EntityCache&lt;Client, long&gt;(
///     () => new MagicOnionEntityStore(layer, session), c => c.Id, monitor, rowId => rowId);
/// </code>
/// </example>
public sealed class MagicOnionEntityStore(
    IMagicOnionLayer layer,
    IDbSession? session = null,
    MessagePackSerializerOptions? options = null) : IEntityStore
{
    // ---------- Lecture ----------

    public async Task<IReadOnlyList<TEntity>> GetAllAsync<TEntity>(CancellationToken ct = default) where TEntity : class
        => DeserializeMany<TEntity>(await CallAsync(l => l.GetAllAsync(Name<TEntity>()), ct), ct);

    public async Task<IReadOnlyList<TEntity>> SelectAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        var (filters, any) = Filters.FromPredicate(predicate);
        return DeserializeMany<TEntity>(await CallAsync(l => l.SelectAsync(Name<TEntity>(), filters, any), ct), ct);
    }

    public async Task<TEntity?> FirstOrDefaultAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => (await SelectPagedAsync(predicate, 1, 1, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<TEntity>> SelectPagedAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize,
        CancellationToken ct = default) where TEntity : class
    {
        Paging.Validate(pageNumber, pageSize);
        var (filters, any) = Filters.FromPredicate(predicate);
        return DeserializeMany<TEntity>(
            await CallAsync(l => l.SelectPagedAsync(Name<TEntity>(), filters, any, pageNumber, pageSize), ct), ct);
    }

    // ---------- Écriture ----------

    public async Task InsertAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var data = await CallAsync(l => l.InsertAsync(Name<TEntity>(), Serialize(entity, ct), ConnectionId<TEntity>()), ct);

        // Le serveur renvoie l'entité insérée : on recopie ses valeurs, dont la clé générée.
        if (data is { Length: > 0 })
            CopyInto(MessagePackSerializer.Deserialize<TEntity>(data, options, ct), entity);
    }

    public async Task<bool> UpdateAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var data = await CallAsync(l => l.UpdateAsync(Name<TEntity>(), Serialize(entity, ct), ConnectionId<TEntity>()), ct);
        return DeserializeBool(data, ct);
    }

    public async Task<bool> DeleteAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var data = await CallAsync(l => l.DeleteAsync(Name<TEntity>(), Serialize(entity, ct), ConnectionId<TEntity>()), ct);
        return DeserializeBool(data, ct);
    }

    // ---------- Transactions ----------

    public Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => throw new NotSupportedException(
            "Les transactions ne traversent pas MagicOnion : chaque écriture est validée séparément côté serveur. " +
            "Pour une opération atomique, exposez une méthode dédiée sur le serveur.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---------- Interne ----------

    private async Task<byte[]?> CallAsync(Func<IMagicOnionLayer, UnaryResult<DBResult>> call, CancellationToken ct)
    {
        var result = await call(layer.WithCancellationToken(ct));
        if (!result.Successful)
            throw new InvalidOperationException(result.Exception ?? "L'appel distant a échoué.");
        return result.Data;
    }

    /// <summary>Nom sous lequel le serveur enregistre le type (voir <see cref="EntityHandlerRegistry.Register(EntityHandler)"/>).</summary>
    private static string Name<TEntity>() => typeof(TEntity).Name;

    private string? ConnectionId<TEntity>()
        => session is not null && session.TryGetSession(typeof(TEntity), out var current) ? current.session : null;

    private byte[] Serialize<TEntity>(TEntity entity, CancellationToken ct)
        => MessagePackSerializer.Serialize(entity, options, ct);

    private IReadOnlyList<TEntity> DeserializeMany<TEntity>(byte[]? data, CancellationToken ct)
        => data is { Length: > 0 }
            ? MessagePackSerializer.Deserialize<TEntity[]>(data, options, ct) ?? []
            : [];

    private bool DeserializeBool(byte[]? data, CancellationToken ct)
        => data is { Length: > 0 } && MessagePackSerializer.Deserialize<bool>(data, options, ct);

    private static void CopyInto<TEntity>(TEntity source, TEntity target)
    {
        if (source is null) return;

        foreach (var property in typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                property.SetValue(target, property.GetValue(source));
        }
    }
}

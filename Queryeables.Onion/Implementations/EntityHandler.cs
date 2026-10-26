using MessagePack;
using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Onion;

/// <summary>
/// Traite côté serveur les requêtes d'un type d'entité, via <see cref="IEntityStore"/> :
/// fonctionne donc avec Dommel comme avec EF. Les erreurs sont renvoyées dans le <see cref="DBResult"/>.
/// </summary>
public abstract class EntityHandler
{
    public abstract Type Type { get; }

    public abstract Task<DBResult> GetAllAsync(CancellationToken ct = default);

    public abstract Task<DBResult> SelectAsync(Dictionary<string, object?> filters, bool any, CancellationToken ct = default);

    public abstract Task<DBResult> SelectPagedAsync(Dictionary<string, object?> filters, bool any,
        int pageNumber, int pageSize, CancellationToken ct = default);

    public abstract Task<DBResult> InsertAsync(byte[] entity, string? connectionId, CancellationToken ct = default);

    public abstract Task<DBResult> UpdateAsync(byte[] entity, string? connectionId, CancellationToken ct = default);

    public abstract Task<DBResult> DeleteAsync(byte[] entity, string? connectionId, CancellationToken ct = default);

    protected static async Task<DBResult> RunAsync(Func<Task<byte[]>> action)
    {
        try
        {
            return new DBResult { Successful = true, Data = await action() };
        }
        catch (Exception ex)
        {
            return new DBResult { Successful = false, Exception = ex.Message };
        }
    }
}

/// <example>
/// <code>
/// registry.Register(new EntityHandler&lt;Client&gt;(() => DommelEntityStore.Create(factory)));
/// </code>
/// </example>
public sealed class EntityHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
    Func<IEntityStore> storeFactory,
    MessagePackSerializerOptions? options = null) : EntityHandler where T : class
{
    public override Type Type => typeof(T);

    public override Task<DBResult> GetAllAsync(CancellationToken ct = default)
        => RunAsync(async () =>
        {
            await using var store = storeFactory();
            return SerializeMany(await store.GetAllAsync<T>(ct), ct);
        });

    public override Task<DBResult> SelectAsync(Dictionary<string, object?> filters, bool any, CancellationToken ct = default)
        => filters.Count == 0
            ? GetAllAsync(ct)
            : RunAsync(async () =>
            {
                var predicate = Filters.ToPredicate<T>(filters, any);
                await using var store = storeFactory();
                return SerializeMany(await store.SelectAsync(predicate, ct), ct);
            });

    public override Task<DBResult> SelectPagedAsync(Dictionary<string, object?> filters, bool any,
        int pageNumber, int pageSize, CancellationToken ct = default)
        => RunAsync(async () =>
        {
            var predicate = Filters.ToPredicate<T>(filters, any);
            await using var store = storeFactory();
            return SerializeMany(await store.SelectPagedAsync(predicate, pageNumber, pageSize, ct), ct);
        });

    public override Task<DBResult> InsertAsync(byte[] entity, string? connectionId, CancellationToken ct = default)
        => RunAsync(async () =>
        {
            var value = Deserialize(entity, ct);
            using (ChangeContext.Begin(connectionId))
            {
                await using var store = storeFactory();
                await store.InsertAsync(value, ct);
            }
            // Renvoyée pour que le client récupère la clé générée.
            return MessagePackSerializer.Serialize(value, options, ct);
        });

    public override Task<DBResult> UpdateAsync(byte[] entity, string? connectionId, CancellationToken ct = default)
        => RunAsync(async () =>
        {
            var value = Deserialize(entity, ct);
            bool updated;
            using (ChangeContext.Begin(connectionId))
            {
                await using var store = storeFactory();
                updated = await store.UpdateAsync(value, ct);
            }
            return MessagePackSerializer.Serialize(updated, options, ct);
        });

    public override Task<DBResult> DeleteAsync(byte[] entity, string? connectionId, CancellationToken ct = default)
        => RunAsync(async () =>
        {
            var value = Deserialize(entity, ct);
            bool deleted;
            using (ChangeContext.Begin(connectionId))
            {
                await using var store = storeFactory();
                deleted = await store.DeleteAsync(value, ct);
            }
            return MessagePackSerializer.Serialize(deleted, options, ct);
        });

    private T Deserialize(byte[] bytes, CancellationToken ct)
        => MessagePackSerializer.Deserialize<T>(bytes, options, ct)
           ?? throw new InvalidOperationException($"Entité {typeof(T).Name} vide.");

    private byte[] SerializeMany(IReadOnlyList<T> entities, CancellationToken ct)
        => MessagePackSerializer.Serialize(entities.ToArray(), options, ct);
}

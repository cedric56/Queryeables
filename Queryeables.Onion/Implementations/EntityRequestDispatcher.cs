namespace Queryeables.Onion;

/// <summary>
/// Aiguille chaque appel de <see cref="IMagicOnionLayer"/> vers le <see cref="EntityHandler"/> du type demandé.
/// Le service MagicOnion côté serveur n'a plus qu'à déléguer :
/// </summary>
/// <example>
/// <code>
/// public class MagicOnionLayerService(EntityRequestDispatcher dispatcher)
///     : ServiceBase&lt;IMagicOnionLayer&gt;, IMagicOnionLayer
/// {
///     public UnaryResult&lt;DBResult&gt; GetAllAsync(string type)
///         => new(dispatcher.GetAllAsync(type, Context.CallContext.CancellationToken));
///     // ... idem pour les autres méthodes
/// }
/// </code>
/// </example>
public sealed class EntityRequestDispatcher(IEntityHandlerRegistry registry)
{
    public Task<DBResult> GetAllAsync(string type, CancellationToken ct = default)
        => Dispatch(type, h => h.GetAllAsync(ct));

    public Task<DBResult> SelectAsync(string type, Dictionary<string, object?> filters, bool any, CancellationToken ct = default)
        => Dispatch(type, h => h.SelectAsync(filters ?? new(), any, ct));

    public Task<DBResult> SelectPagedAsync(string type, Dictionary<string, object?> filters, bool any,
        int pageNumber, int pageSize, CancellationToken ct = default)
        => Dispatch(type, h => h.SelectPagedAsync(filters ?? new(), any, pageNumber, pageSize, ct));

    public Task<DBResult> InsertAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
        => Dispatch(type, h => h.InsertAsync(entity, connectionId, ct));

    public Task<DBResult> UpdateAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
        => Dispatch(type, h => h.UpdateAsync(entity, connectionId, ct));

    public Task<DBResult> DeleteAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
        => Dispatch(type, h => h.DeleteAsync(entity, connectionId, ct));

    private Task<DBResult> Dispatch(string type, Func<EntityHandler, Task<DBResult>> call)
        => registry.TryGet(type, out var handler)
            ? call(handler)
            : Task.FromResult(new DBResult { Successful = false, Exception = $"Type non enregistré : {type}." });
}

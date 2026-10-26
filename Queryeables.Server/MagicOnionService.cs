using MagicOnion;
using MagicOnion.Server;
using Queryeables.Onion;

namespace Queryeables.Server;

/// <summary>
/// Service MagicOnion d'accès aux entités : chaque appel est transmis au <see cref="EntityHandler"/> du type demandé.
/// Seuls les types enregistrés dans <see cref="IEntityHandlerRegistry"/> sont accessibles, et aucun SQL ne vient du client.
/// </summary>
/// <example>
/// <code>
/// // Dans l'application serveur :
/// public sealed class AppMagicOnionService(EntityRequestDispatcher dispatcher) : MagicOnionService(dispatcher);
///
/// builder.Services.AddSingleton&lt;IEntityHandlerRegistry&gt;(sp =>
/// {
///     var registry = new EntityHandlerRegistry();
///     registry.Register(new EntityHandler&lt;Client&gt;(() => DommelEntityStore.Create(sp.GetRequiredService&lt;DbProviderFactory&gt;())));
///     return registry;
/// });
/// builder.Services.AddSingleton&lt;EntityRequestDispatcher&gt;();
/// </code>
/// </example>
public abstract class MagicOnionService(EntityRequestDispatcher dispatcher)
    : ServiceBase<IMagicOnionLayer>, IMagicOnionLayer
{
    private CancellationToken Aborted => Context.CallContext.CancellationToken;

    public UnaryResult<DBResult> GetAllAsync(string type)
        => new(dispatcher.GetAllAsync(type, Aborted));

    public UnaryResult<DBResult> SelectAsync(string type, Dictionary<string, object?> filters, bool any)
        => new(dispatcher.SelectAsync(type, filters, any, Aborted));

    public UnaryResult<DBResult> SelectPagedAsync(string type, Dictionary<string, object?> filters, bool any, int pageNumber, int pageSize)
        => new(dispatcher.SelectPagedAsync(type, filters, any, pageNumber, pageSize, Aborted));

    public UnaryResult<DBResult> InsertAsync(string type, byte[] entity, string? connectionId)
        => new(dispatcher.InsertAsync(type, entity, connectionId, Aborted));

    public UnaryResult<DBResult> UpdateAsync(string type, byte[] entity, string? connectionId)
        => new(dispatcher.UpdateAsync(type, entity, connectionId, Aborted));

    public UnaryResult<DBResult> DeleteAsync(string type, byte[] entity, string? connectionId)
        => new(dispatcher.DeleteAsync(type, entity, connectionId, Aborted));
}

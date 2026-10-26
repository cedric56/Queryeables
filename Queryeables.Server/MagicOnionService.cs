using Grpc.Core;
using MagicOnion;
using MagicOnion.Server;
using Queryeables.Onion;

namespace Queryeables.Server;

/// <summary>
/// Service MagicOnion d'accès aux entités : chaque appel est transmis au <see cref="EntityHandler"/> du type demandé,
/// avec l'utilisateur authentifié par ASP.NET Core, pour que la règle d'accès de chaque type s'applique.
/// Seuls les types enregistrés dans <see cref="IEntityHandlerRegistry"/> sont accessibles, et aucun SQL ne vient du client.
/// </summary>
/// <example>
/// <code>
/// // Dans l'application serveur :
/// public sealed class AppMagicOnionService(EntityRequestDispatcher dispatcher) : MagicOnionService(dispatcher);
///
/// builder.Services.AddSingleton&lt;IEntityHandlerRegistry&gt;(sp =>
/// {
///     var factory = sp.GetRequiredService&lt;DbProviderFactory&gt;();
///     var registry = new EntityHandlerRegistry();
///     registry.Register(new EntityHandler&lt;Client&gt;(() => DommelEntityStore.Create(factory), EntityAccessPolicies.Authenticated));
///     return registry;
/// });
/// // Le store transactionnel active les lots (transactions distantes).
/// builder.Services.AddSingleton(sp => new EntityRequestDispatcher(
///     sp.GetRequiredService&lt;IEntityHandlerRegistry&gt;(),
///     () => DommelEntityStore.Create(sp.GetRequiredService&lt;DbProviderFactory&gt;())));
///
/// // Authentification ASP.NET Core habituelle (JWT, cookies...) : l'utilisateur arrive dans les règles d'accès.
/// builder.Services.AddAuthentication(...);
/// app.UseAuthentication();
/// </code>
/// </example>
public abstract class MagicOnionService(EntityRequestDispatcher dispatcher)
    : ServiceBase<IMagicOnionLayer>, IMagicOnionLayer
{
    private CancellationToken Aborted => Context.CallContext.CancellationToken;

    /// <summary>Utilisateur authentifié par ASP.NET Core pour cet appel.</summary>
    private EntityRequestContext Caller(string? connectionId = null)
        => new(Context.CallContext.GetHttpContext().User, connectionId);

    public UnaryResult<DBResult> GetAllAsync(string type)
        => new(dispatcher.GetAllAsync(type, Caller(), Aborted));

    public UnaryResult<DBResult> SelectAsync(string type, FilterCondition[] conditions, bool any)
        => new(dispatcher.SelectAsync(type, conditions, any, Caller(), Aborted));

    public UnaryResult<DBResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize)
        => new(dispatcher.SelectPagedAsync(type, conditions, any, pageNumber, pageSize, Caller(), Aborted));

    public UnaryResult<DBResult> InsertAsync(string type, byte[] entity, string? connectionId)
        => new(dispatcher.InsertAsync(type, entity, Caller(connectionId), Aborted));

    public UnaryResult<DBResult> UpdateAsync(string type, byte[] entity, string? connectionId)
        => new(dispatcher.UpdateAsync(type, entity, Caller(connectionId), Aborted));

    public UnaryResult<DBResult> DeleteAsync(string type, byte[] entity, string? connectionId)
        => new(dispatcher.DeleteAsync(type, entity, Caller(connectionId), Aborted));

    public UnaryResult<DBResult> ExecuteBatchAsync(BatchOperation[] operations)
        => new(dispatcher.ExecuteBatchAsync(operations, Caller(), Aborted));
}

using Grpc.Core;
using MagicOnion;
using MagicOnion.Server;
using Queryeables.Onion;

namespace Queryeables.Server;

/// <summary>
/// MagicOnion entity access service: each call is forwarded to the <see cref="EntityHandler"/> of the requested type,
/// with the user authenticated by ASP.NET Core, so that the access policy of each type applies.
/// Only the types registered in <see cref="IEntityHandlerRegistry"/> can be reached, and no SQL comes from the client.
/// </summary>
/// <example>
/// <code>
/// // In the server application:
/// public sealed class AppMagicOnionService(EntityRequestDispatcher dispatcher) : MagicOnionService(dispatcher);
///
/// builder.Services.AddSingleton&lt;IEntityHandlerRegistry&gt;(sp =>
/// {
///     var factory = sp.GetRequiredService&lt;DbProviderFactory&gt;();
///     var registry = new EntityHandlerRegistry();
///     registry.Register(new EntityHandler&lt;Customer&gt;(() => DommelEntityStore.Create(factory).WithSyncConventions(), EntityAccessPolicies.Authenticated));
///     return registry;
/// });
/// // WithSyncConventions applies [LastModified] and [SoftDelete] on the server.
/// // The transactional store enables batches (remote transactions).
/// builder.Services.AddSingleton(sp => new EntityRequestDispatcher(
///     sp.GetRequiredService&lt;IEntityHandlerRegistry&gt;(),
///     () => DommelEntityStore.Create(sp.GetRequiredService&lt;DbProviderFactory&gt;()).WithSyncConventions()));
///
/// // Usual ASP.NET Core authentication (JWT, cookies...): the user reaches the access policies.
/// builder.Services.AddAuthentication(...);
/// app.UseAuthentication();
/// </code>
/// </example>
public abstract class MagicOnionService(EntityRequestDispatcher dispatcher)
    : ServiceBase<IMagicOnionLayer>, IMagicOnionLayer
{
    private CancellationToken Aborted => Context.CallContext.CancellationToken;

    /// <summary>User authenticated by ASP.NET Core for this call.</summary>
    private EntityRequestContext Caller(string? connectionId = null)
        => new(Context.CallContext.GetHttpContext().User, connectionId);

    public UnaryResult<DBResultMessage> GetAllAsync(string type)
        => new(dispatcher.GetAllAsync(type, Caller(), Aborted));

    public UnaryResult<DBResultMessage> SelectAsync(string type, FilterCondition[] conditions, bool any)
        => new(dispatcher.SelectAsync(type, conditions, any, Caller(), Aborted));

    public UnaryResult<DBResultMessage> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize)
        => new(dispatcher.SelectPagedAsync(type, conditions, any, pageNumber, pageSize, Caller(), Aborted));

    public UnaryResult<DBResultMessage> GetChangesAsync(string type, DateTime sinceUtc)
        => new(dispatcher.GetChangesAsync(type, sinceUtc, Caller(), Aborted));

    public UnaryResult<DBResultMessage> InsertAsync(string type, byte[] entity, string? connectionId)
        => new(dispatcher.InsertAsync(type, entity, Caller(connectionId), Aborted));

    public UnaryResult<DBResultMessage> UpdateAsync(string type, byte[] entity, string? connectionId)
        => new(dispatcher.UpdateAsync(type, entity, Caller(connectionId), Aborted));

    public UnaryResult<DBResultMessage> DeleteAsync(string type, byte[] entity, string? connectionId)
        => new(dispatcher.DeleteAsync(type, entity, Caller(connectionId), Aborted));

    public UnaryResult<DBResultMessage> ExecuteBatchAsync(BatchOperation[] operations)
        => new(dispatcher.ExecuteBatchAsync(operations, Caller(), Aborted));
}

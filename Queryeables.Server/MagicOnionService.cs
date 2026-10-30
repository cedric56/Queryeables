using Grpc.Core;
using MagicOnion;
using MagicOnion.Server;
using Queryeables.Onion;
using Queryeables.Remote;

namespace Queryeables.Server;

/// <summary>
/// MagicOnion entity access service: each call is forwarded to <see cref="EntityRequestDispatcher"/>, with the user
/// authenticated by ASP.NET Core, so that the access policy of each type applies. Only the exposed types can be reached,
/// and no SQL comes from the client.
/// </summary>
/// <example>
/// <code>
/// // In the server application: MagicOnion finds the service by this class.
/// public sealed class AppMagicOnionService(EntityRequestDispatcher dispatcher) : MagicOnionService(dispatcher);
///
/// builder.Services.AddMagicOnion();
/// builder.Services.UseQueryeables(q => q
///     .UseSqlite("Data Source=app.db")
///     .AddGeneratedEntities()
///     .AddEntityServer(MessagePackEntitySerializer.Default, server => server
///         .Expose&lt;Person&gt;(EntityAccessPolicies.Authenticated)
///         .Expose&lt;Contact&gt;(EntityAccessPolicies.Authenticated)));
///
/// app.MapMagicOnionService();
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
        => Reply(dispatcher.GetAllAsync(type, Caller(), Aborted));

    public UnaryResult<DBResultMessage> SelectAsync(string type, FilterConditionMessage[] conditions, bool any)
        => Reply(dispatcher.SelectAsync(type, FilterConditionMessage.ToConditions(conditions), any, Caller(), Aborted));

    public UnaryResult<DBResultMessage> SelectPagedAsync(string type, FilterConditionMessage[] conditions, bool any, int pageNumber, int pageSize)
        => Reply(dispatcher.SelectPagedAsync(type, FilterConditionMessage.ToConditions(conditions), any, pageNumber, pageSize, Caller(), Aborted));

    public UnaryResult<DBResultMessage> GetChangesAsync(string type, DateTime sinceUtc)
        => Reply(dispatcher.GetChangesAsync(type, sinceUtc, Caller(), Aborted));

    public UnaryResult<DBResultMessage> InsertAsync(string type, byte[] entity, string? connectionId)
        => Reply(dispatcher.InsertAsync(type, entity, Caller(connectionId), Aborted));

    public UnaryResult<DBResultMessage> UpdateAsync(string type, byte[] entity, string? connectionId)
        => Reply(dispatcher.UpdateAsync(type, entity, Caller(connectionId), Aborted));

    public UnaryResult<DBResultMessage> DeleteAsync(string type, byte[] entity, string? connectionId)
        => Reply(dispatcher.DeleteAsync(type, entity, Caller(connectionId), Aborted));

    public UnaryResult<DBResultMessage> ExecuteBatchAsync(BatchOperationMessage[] operations)
        => Reply(dispatcher.ExecuteBatchAsync(BatchOperationMessage.ToOperations(operations), Caller(), Aborted));

    private static UnaryResult<DBResultMessage> Reply(Task<RemoteResult> call) => new(ToMessageAsync(call));

    private static async Task<DBResultMessage> ToMessageAsync(Task<RemoteResult> call) => DBResultMessage.From(await call);
}

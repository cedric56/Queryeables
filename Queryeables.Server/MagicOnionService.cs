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

    public UnaryResult<EntityResultMessage> GetAllAsync(string type)
        => Reply(dispatcher.GetAllAsync(type, Caller(), Aborted));

    public UnaryResult<EntityResultMessage> SelectAsync(string type, FilterConditionMessage[] conditions, bool any)
        => Reply(dispatcher.SelectAsync(type, FilterConditionMessage.ToConditions(conditions), any, Caller(), Aborted));

    public UnaryResult<EntityResultMessage> SelectPagedAsync(string type, FilterConditionMessage[] conditions, bool any, int pageNumber, int pageSize)
        => Reply(dispatcher.SelectPagedAsync(type, FilterConditionMessage.ToConditions(conditions), any, pageNumber, pageSize, Caller(), Aborted));

    public UnaryResult<EntityResultMessage> SelectSortedPageAsync(string type, FilterConditionMessage[] conditions, bool any, int pageNumber,
        int pageSize, SortKeyMessage[] sort)
        => Reply(dispatcher.SelectPagedAsync(type, FilterConditionMessage.ToConditions(conditions), any, pageNumber, pageSize,
            SortKeyMessage.ToKeys(sort), Caller(), Aborted));

    public UnaryResult<EntityResultMessage> GetChangesAsync(string type, DateTime sinceUtc)
        => Reply(dispatcher.GetChangesAsync(type, sinceUtc, Caller(), Aborted));

    public UnaryResult<EntityResultMessage> InsertAsync(string type, byte[] entity, string? connectionId)
        => Reply(dispatcher.InsertAsync(type, entity, Caller(connectionId), Aborted));

    public UnaryResult<EntityResultMessage> UpdateAsync(string type, byte[] entity, string? connectionId)
        => Reply(dispatcher.UpdateAsync(type, entity, Caller(connectionId), Aborted));

    public UnaryResult<EntityResultMessage> DeleteAsync(string type, byte[] entity, string? connectionId)
        => Reply(dispatcher.DeleteAsync(type, entity, Caller(connectionId), Aborted));

    public UnaryResult<EntityResultMessage> ExecuteBatchAsync(BatchOperationMessage[] operations)
        => Reply(dispatcher.ExecuteBatchAsync(BatchOperationMessage.ToOperations(operations), Caller(), Aborted));

    private static UnaryResult<EntityResultMessage> Reply(Task<TransportResult> call) => new(ToMessageAsync(call));

    private static async Task<EntityResultMessage> ToMessageAsync(Task<TransportResult> call) => EntityResultMessage.From(await call);
}

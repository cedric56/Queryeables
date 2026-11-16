using Grpc.Core;
using MagicOnion;
using MagicOnion.Server;
using Queryeables.Abstraction;
using Queryeables.MagicOnion;
using Queryeables.Remote;

namespace Queryeables.MagicOnion.Server;

/// <summary>
/// MagicOnion service of the replicas: each read is forwarded to <see cref="EntityRequestDispatcher"/>, with the user
/// authenticated by ASP.NET Core, so that the access policy and the row filter of each type apply. Only the exposed types
/// can be reached; no SQL comes from the client, and nothing is written.
/// </summary>
/// <example>
/// <code>
/// builder.Services.UseQueryeables(q => q
///     .UseSqlite("Data Source=app.db")
///     .AddGeneratedEntities()
///     .AddMagicOnionServer(server => server.ExposeGenerated(EntityAccessPolicies.Authenticated)));
///
/// app.MapQueryeables();       // maps QueryeablesService, which derives from this class
/// </code>
/// </example>
public abstract class MagicOnionService(EntityRequestDispatcher dispatcher)
    : ServiceBase<IMagicOnionLayer>, IMagicOnionLayer
{
    private CancellationToken Aborted => Context.CallContext.CancellationToken;

    /// <summary>User authenticated by ASP.NET Core for this call.</summary>
    private EntityRequestContext Caller() => new(Context.CallContext.GetHttpContext().User);

    public UnaryResult<EntityResultMessage> GetAllAsync(string type)
        => Reply(() => dispatcher.GetAllAsync(type, Caller(), Aborted));

    public UnaryResult<EntityResultMessage> SelectAsync(string type, FilterConditionMessage[] conditions)
        => Reply(() => dispatcher.SelectAsync(type, FilterConditionMessage.ToConditions(conditions), Caller(), Aborted));

    public UnaryResult<EntityResultMessage> GetChangesAsync(string type, DateTime sinceUtc)
        => Reply(() => dispatcher.GetChangesAsync(type, sinceUtc, Caller(), Aborted));

    /// <summary>
    /// The answer to a call. A malformed message (a null request, a null element in an array) is answered with
    /// InvalidRequest rather than a gRPC error without a cause.
    /// </summary>
    private static UnaryResult<EntityResultMessage> Reply(Func<Task<TransportResult>> call)
    {
        Task<TransportResult> pending;
        try
        {
            pending = call();
        }
        catch (ArgumentException ex)
        {
            return new(Task.FromResult(EntityResultMessage.From(TransportResult.Failure(EntityErrorCode.InvalidRequest, ex.Message))));
        }
        return new(ToMessageAsync(pending));
    }

    private static async Task<EntityResultMessage> ToMessageAsync(Task<TransportResult> call) => EntityResultMessage.From(await call);
}

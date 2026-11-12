using MagicOnion;
using Queryeables.Remote;

namespace Queryeables.MagicOnion;

/// <summary>
/// MagicOnion service for remote entity access: the MagicOnion form of <see cref="IEntityTransport"/>.
/// The server implements it by delegating to <see cref="EntityRequestDispatcher"/> (<c>MagicOnionService</c>);
/// the client uses it through <see cref="MagicOnionTransport"/> and <see cref="RemoteEntityStore"/>.
/// <para>
/// Filters are "property operator value" conditions (<see cref="FilterConditionMessage"/>),
/// all combined with AND (<c>any</c> = false) or all with OR (<c>any</c> = true), or nested in postfix order
/// (<see cref="Queryeables.Remote.FilterOperator.And"/> and <see cref="Queryeables.Remote.FilterOperator.Or"/> nodes).
/// No SQL goes over the wire: only the types registered on the server can be reached, according to their access policies.
/// </para>
/// <para>On failure, <see cref="EntityResultMessage.ErrorCode"/> gives the kind of error.</para>
/// </summary>
public interface IMagicOnionLayer : IService<IMagicOnionLayer>
{
    UnaryResult<EntityResultMessage> GetAllAsync(string type);

    UnaryResult<EntityResultMessage> SelectAsync(string type, FilterConditionMessage[] conditions, bool any);

    UnaryResult<EntityResultMessage> SelectPagedAsync(string type, FilterConditionMessage[] conditions, bool any, int pageNumber, int pageSize);

    /// <summary>A page in the given order, sorted by the server.</summary>
    UnaryResult<EntityResultMessage> SelectSortedPageAsync(string type, FilterConditionMessage[] conditions, bool any, int pageNumber,
        int pageSize, SortKeyMessage[] sort);

    /// <summary>
    /// Rows modified since <paramref name="sinceUtc"/>, soft-deleted ones included (incremental synchronization).
    /// Requires a [LastModified] property on the type.
    /// </summary>
    UnaryResult<EntityResultMessage> GetChangesAsync(string type, DateTime sinceUtc);

    /// <summary>Returns the inserted entity, with its generated key and version.</summary>
    UnaryResult<EntityResultMessage> InsertAsync(string type, byte[] entity, string? connectionId);

    /// <summary>Returns the updated entity (new version), or empty data when the row does not exist.</summary>
    UnaryResult<EntityResultMessage> UpdateAsync(string type, byte[] entity, string? connectionId);

    /// <summary>Returns true if the row was deleted.</summary>
    UnaryResult<EntityResultMessage> DeleteAsync(string type, byte[] entity, string? connectionId);

    /// <summary>
    /// Runs every write in a single transaction: all succeed, or none is applied.
    /// Returns one result per operation (the entity for an insert or an update, true for a delete).
    /// </summary>
    UnaryResult<EntityResultMessage> ExecuteBatchAsync(BatchOperationMessage[] operations);

    /// <summary>A batch applied once per <paramref name="operationId"/>: sent again after a lost answer, it returns the first result.</summary>
    UnaryResult<EntityResultMessage> ExecuteBatchOnceAsync(Guid operationId, BatchOperationMessage[] operations);

    /// <summary>
    /// A write applied once per <paramref name="operationId"/>: sent again after a lost answer, it returns the first result
    /// (offline synchronization).
    /// </summary>
    UnaryResult<EntityResultMessage> WriteOnceAsync(Guid operationId, BatchOperationKind kind, string type, byte[] entity, string? connectionId);

    /// <summary>Counts, aggregates, projections, patches, writes of many rows, graph saves (<see cref="RemoteRequestKind"/>).</summary>
    UnaryResult<EntityResultMessage> ExecuteAsync(string type, RemoteRequestMessage request);
}

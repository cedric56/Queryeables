using Queryeables.Abstraction;

namespace Queryeables.Remote;

/// <summary>
/// What a transport implements on the client to reach the server: one method per operation, entities as bytes
/// (see <see cref="IEntitySerializer"/>), answers as <see cref="RemoteResult"/>. <see cref="RemoteEntityStore"/> does the rest.
/// </summary>
/// <remarks>
/// On the server, the transport forwards each call to <see cref="EntityRequestDispatcher"/>, whose methods have the same
/// shape. <see cref="EntityRequestDispatcher.AsRemote"/> connects the two in-process, without a network.
/// Filters are "property operator value" conditions (<see cref="FilterCondition"/>), all combined with AND
/// (<c>any</c> = false) or all with OR (<c>any</c> = true), or nested in postfix order with
/// <see cref="FilterOperator.And"/> and <see cref="FilterOperator.Or"/> nodes: no SQL and no expression goes over the wire.
/// </remarks>
public interface IEntityRemote
{
    Task<RemoteResult> GetAllAsync(string type, CancellationToken ct = default);

    Task<RemoteResult> SelectAsync(string type, FilterCondition[] conditions, bool any, CancellationToken ct = default);

    Task<RemoteResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// A page in the given order, sorted by the server. Transports written before sorting existed only support an empty
    /// sort.
    /// </summary>
    Task<RemoteResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        SortKey[] sort, CancellationToken ct = default)
        => sort is not { Length: > 0 }
            ? SelectPagedAsync(type, conditions, any, pageNumber, pageSize, ct)
            : Task.FromResult(RemoteResult.Failure(EntityErrorCode.NotSupported, "This transport does not support sorted pages."));

    /// <summary>Rows modified since <paramref name="sinceUtc"/>, soft-deleted ones included (incremental synchronization).</summary>
    Task<RemoteResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default);

    /// <summary>Returns the inserted entity, with its generated key and version.</summary>
    Task<RemoteResult> InsertAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default);

    /// <summary>Returns the updated entity (new version), or empty data when the row does not exist.</summary>
    Task<RemoteResult> UpdateAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default);

    /// <summary>Returns true if the row was deleted.</summary>
    Task<RemoteResult> DeleteAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default);

    /// <summary>
    /// Runs every write in a single transaction: all succeed, or none is applied.
    /// Returns one result per operation (the entity for an insert or an update, true for a delete).
    /// </summary>
    Task<RemoteResult> ExecuteBatchAsync(BatchOperation[] operations, CancellationToken ct = default);
}

using Queryeables.Abstraction;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Queryeables.Remote;

/// <summary>
/// Validates the server certificate; return true to accept it.
/// </summary>
public delegate bool CertificateValidator(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors);


/// <summary>
/// What a transport implements on the client to reach the server: one method per operation, entities as bytes
/// (see <see cref="IEntitySerializer"/>), answers as <see cref="TransportResult"/>. <see cref="RemoteEntityStore"/> does the rest.
/// </summary>
/// <remarks>
/// On the server, the transport forwards each call to <see cref="EntityRequestDispatcher"/>, whose methods have the same
/// shape. <see cref="EntityRequestDispatcher.AsTransport"/> connects the two in-process, without a network.
/// Filters are "property operator value" conditions (<see cref="FilterCondition"/>), all combined with AND
/// (<c>any</c> = false) or all with OR (<c>any</c> = true), or nested in postfix order with
/// <see cref="FilterOperator.And"/> and <see cref="FilterOperator.Or"/> nodes: no SQL and no expression goes over the wire.
/// </remarks>
public interface IEntityTransport
{
    Task<TransportResult> GetAllAsync(string type, CancellationToken ct = default);

    Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, bool any, CancellationToken ct = default);

    Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// A page in the given order, sorted by the server. Transports written before sorting existed only support an empty
    /// sort.
    /// </summary>
    Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        SortKey[] sort, CancellationToken ct = default)
        => sort is not { Length: > 0 }
            ? SelectPagedAsync(type, conditions, any, pageNumber, pageSize, ct)
            : Task.FromResult(TransportResult.Failure(EntityErrorCode.NotSupported, "This transport does not support sorted pages."));

    /// <summary>Rows modified since <paramref name="sinceUtc"/>, soft-deleted ones included (incremental synchronization).</summary>
    Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default);

    /// <summary>Returns the inserted entity, with its generated key and version.</summary>
    Task<TransportResult> InsertAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default);

    /// <summary>Returns the updated entity (new version), or empty data when the row does not exist.</summary>
    Task<TransportResult> UpdateAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default);

    /// <summary>Returns true if the row was deleted.</summary>
    Task<TransportResult> DeleteAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default);

    /// <summary>
    /// Runs every write in a single transaction: all succeed, or none is applied.
    /// Returns one result per operation (the entity for an insert or an update, true for a delete).
    /// </summary>
    Task<TransportResult> ExecuteBatchAsync(BatchOperation[] operations, CancellationToken ct = default);

    /// <summary>
    /// <see cref="ExecuteBatchAsync"/>, applied once however many times it is sent with the same
    /// <paramref name="operationId"/>: a commit sent again after a lost answer gets the first result back, and inserts
    /// nothing twice. A transport that cannot carry the id answers NotSupported: the client then sends a plain batch, once.
    /// </summary>
    Task<TransportResult> ExecuteBatchOnceAsync(Guid operationId, BatchOperation[] operations, CancellationToken ct = default)
        => Task.FromResult(TransportResult.Failure(EntityErrorCode.NotSupported, "This transport does not carry operation ids for batches."));

    /// <summary>
    /// A write that the server applies once, however many times it is sent with the same <paramref name="operationId"/>:
    /// a retry after a lost answer gets the first result back (see <see cref="IIdempotencyStore"/>). Used by the offline
    /// synchronization. A transport that cannot carry the id falls back to a plain write, without that guarantee.
    /// </summary>
    Task<TransportResult> WriteOnceAsync(Guid operationId, BatchOperationKind kind, string type, byte[] entity, string? connectionId,
        CancellationToken ct = default)
        => kind switch
        {
            BatchOperationKind.Insert => InsertAsync(type, entity, connectionId, ct),
            BatchOperationKind.Update => UpdateAsync(type, entity, connectionId, ct),
            _ => DeleteAsync(type, entity, connectionId, ct),
        };
}

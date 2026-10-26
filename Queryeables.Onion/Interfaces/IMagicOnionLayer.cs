using MagicOnion;

namespace Queryeables.Onion;

/// <summary>
/// Remote entity access service. The server implements it by delegating to
/// <see cref="EntityRequestDispatcher"/>; the client uses it through <see cref="MagicOnionEntityStore"/>.
/// <para>
/// Filters are "property operator value" conditions (<see cref="FilterCondition"/>),
/// all combined with AND (<c>any</c> = false) or all with OR (<c>any</c> = true).
/// No SQL goes over the wire: only the types registered on the server can be reached, according to their access policies.
/// </para>
/// <para>On failure, <see cref="DBResultMessage.ErrorCode"/> gives the kind of error.</para>
/// </summary>
public interface IMagicOnionLayer : IService<IMagicOnionLayer>
{
    UnaryResult<DBResultMessage> GetAllAsync(string type);

    UnaryResult<DBResultMessage> SelectAsync(string type, FilterCondition[] conditions, bool any);

    UnaryResult<DBResultMessage> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize);

    /// <summary>
    /// Rows modified since <paramref name="sinceUtc"/>, soft-deleted ones included (incremental synchronization).
    /// Requires a [LastModified] property on the type.
    /// </summary>
    UnaryResult<DBResultMessage> GetChangesAsync(string type, DateTime sinceUtc);

    /// <summary>Returns the inserted entity, with its generated key and version.</summary>
    UnaryResult<DBResultMessage> InsertAsync(string type, byte[] entity, string? connectionId);

    /// <summary>Returns the updated entity (new version), or empty data when the row does not exist.</summary>
    UnaryResult<DBResultMessage> UpdateAsync(string type, byte[] entity, string? connectionId);

    /// <summary>Returns true if the row was deleted.</summary>
    UnaryResult<DBResultMessage> DeleteAsync(string type, byte[] entity, string? connectionId);

    /// <summary>
    /// Runs every write in a single transaction: all succeed, or none is applied.
    /// Returns one result per operation (the entity for an insert or an update, true for a delete).
    /// </summary>
    UnaryResult<DBResultMessage> ExecuteBatchAsync(BatchOperation[] operations);
}

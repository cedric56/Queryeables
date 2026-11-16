using MagicOnion;
using Queryeables.Remote;

namespace Queryeables.MagicOnion;

/// <summary>
/// MagicOnion service for the replicas: the MagicOnion form of <see cref="IEntityTransport"/>.
/// The server implements it by delegating to <see cref="EntityRequestDispatcher"/> (<c>MagicOnionService</c>);
/// the client uses it through <see cref="MagicOnionTransport"/> and <see cref="RemoteEntityStore"/>.
/// <para>
/// A row is read by its key ("property = value" conditions, <see cref="FilterConditionMessage"/>). No SQL goes over the
/// wire and nothing is written: only the types registered on the server can be reached, according to their access policies.
/// </para>
/// <para>On failure, <see cref="EntityResultMessage.ErrorCode"/> gives the kind of error.</para>
/// </summary>
public interface IMagicOnionLayer : IService<IMagicOnionLayer>
{
    /// <summary>Every row of the type the user may read.</summary>
    UnaryResult<EntityResultMessage> GetAllAsync(string type);

    /// <summary>The rows of one key (one condition per column of the key).</summary>
    UnaryResult<EntityResultMessage> SelectAsync(string type, FilterConditionMessage[] conditions);

    /// <summary>Rows modified since <paramref name="sinceUtc"/>, soft-deleted ones included.</summary>
    UnaryResult<EntityResultMessage> GetChangesAsync(string type, DateTime sinceUtc);
}

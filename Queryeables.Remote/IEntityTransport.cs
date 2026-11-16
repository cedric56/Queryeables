using Queryeables.Abstraction;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Queryeables.Remote;

/// <summary>
/// Validates the server certificate; return true to accept it.
/// </summary>
public delegate bool CertificateValidator(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors);


/// <summary>
/// What a transport implements on the client to reach the server: the reads of a replica, entities as bytes
/// (see <see cref="IEntitySerializer"/>), answers as <see cref="TransportResult"/>. <see cref="RemoteEntityStore"/> does the rest.
/// </summary>
/// <remarks>
/// On the server, the transport forwards each call to <see cref="EntityRequestDispatcher"/>, whose methods have the same
/// shape. <see cref="EntityRequestDispatcher.AsTransport"/> connects the two in-process, without a network. A row is read by
/// its key: "property = value" conditions (<see cref="FilterCondition"/>), one per column of the key. No SQL and no
/// expression goes over the wire, and nothing is written through it.
/// </remarks>
public interface IEntityTransport
{
    /// <summary>Every row of the type the user may read.</summary>
    Task<TransportResult> GetAllAsync(string type, CancellationToken ct = default);

    /// <summary>The rows of one key, among those the user may read.</summary>
    Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, CancellationToken ct = default);

    /// <summary>Rows modified since <paramref name="sinceUtc"/>, soft-deleted ones included (incremental synchronization).</summary>
    Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default);
}

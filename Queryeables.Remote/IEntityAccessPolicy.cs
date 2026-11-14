using System.Security.Claims;

namespace Queryeables.Remote;

public enum EntityOperation
{
    Read,
    Insert,
    Update,
    Delete,
}

/// <summary>Context of a remote call: who is calling, and from which notification connection.</summary>
/// <param name="User">User authenticated by the server (null when the call is not authenticated).</param>
/// <param name="ConnectionId">Notification session of the client, so that it ignores its own changes.</param>
public sealed record EntityRequestContext(ClaimsPrincipal? User = null, string? ConnectionId = null)
{
    public static EntityRequestContext Anonymous { get; } = new();

    /// <summary>The exposed types, set by the dispatcher: the children of a graph follow the rules of their own type.</summary>
    internal IEntityHandlerRegistry? Handlers { get; init; }

    public EntityRequestContext WithConnection(string? connectionId) => this with { ConnectionId = connectionId };
}

/// <param name="Entity">
/// The entity concerned by a write; null for a read (type-level check). For an insert it is the content the client sent.
/// For an update or a delete, the policy is asked twice: for the content the client sent, then for the stored row it
/// targets (unless <see cref="IEntityAccessPolicy.ChecksEntities"/> is false), so that a rule on who owns a row
/// (<c>e.OwnerId == me</c>) protects the rows of others. A patch, an update or a deletion of many rows presents the stored
/// rows too. Reads are filtered by a row filter (<c>RowLevelSecurity</c>), not by this.
/// </param>
public sealed record EntityAccessRequest(EntityOperation Operation, Type EntityType, object? Entity, ClaimsPrincipal? User);

/// <summary>
/// Decides whether a user may perform an operation on an entity type. Every <see cref="EntityHandler{T}"/>
/// requires one: exposing a type without an explicit access policy is not possible.
/// </summary>
public interface IEntityAccessPolicy
{
    ValueTask<bool> IsAllowedAsync(EntityAccessRequest request, CancellationToken ct = default);

    /// <summary>
    /// True when the decision depends on the entity: an update or a delete then reads the stored row to ask about it too.
    /// False for a rule on the user and the operation only, which spares that read.
    /// </summary>
    bool ChecksEntities => true;
}

/// <example>
/// <code>
/// // Read for any signed-in user, write for the "admin" role:
/// var policy = EntityAccessPolicies.From(r =>
///     r.User?.Identity?.IsAuthenticated == true &amp;&amp;
///     (r.Operation == EntityOperation.Read || r.User.IsInRole("admin")));
/// </code>
/// </example>
public static class EntityAccessPolicies
{
    /// <summary>Everything is allowed, even without authentication. For tests or a closed network.</summary>
    public static IEntityAccessPolicy AllowAll { get; } = From(_ => true, checksEntities: false);

    /// <summary>Everything is allowed to an authenticated user.</summary>
    public static IEntityAccessPolicy Authenticated { get; } = From(r => r.User?.Identity?.IsAuthenticated == true, checksEntities: false);

    /// <summary>Read for an authenticated user, no write.</summary>
    public static IEntityAccessPolicy ReadOnly { get; } = From(r =>
        r.Operation == EntityOperation.Read && r.User?.Identity?.IsAuthenticated == true, checksEntities: false);

    /// <param name="checksEntities">False when <paramref name="rule"/> never reads <see cref="EntityAccessRequest.Entity"/>.</param>
    public static IEntityAccessPolicy From(Func<EntityAccessRequest, bool> rule, bool checksEntities = true)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new DelegatePolicy((request, _) => ValueTask.FromResult(rule(request)), checksEntities);
    }

    /// <param name="checksEntities">False when <paramref name="rule"/> never reads <see cref="EntityAccessRequest.Entity"/>.</param>
    public static IEntityAccessPolicy FromAsync(Func<EntityAccessRequest, CancellationToken, ValueTask<bool>> rule, bool checksEntities = true)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new DelegatePolicy(rule, checksEntities);
    }

    private sealed class DelegatePolicy(Func<EntityAccessRequest, CancellationToken, ValueTask<bool>> rule, bool checksEntities) : IEntityAccessPolicy
    {
        public ValueTask<bool> IsAllowedAsync(EntityAccessRequest request, CancellationToken ct = default) => rule(request, ct);

        public bool ChecksEntities => checksEntities;
    }
}

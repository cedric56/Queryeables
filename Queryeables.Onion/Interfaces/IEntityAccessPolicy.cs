using System.Security.Claims;

namespace Queryeables.Onion;

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

    public EntityRequestContext WithConnection(string? connectionId) => this with { ConnectionId = connectionId };
}

/// <param name="Entity">The entity concerned by a write; null for a read (type-level check).</param>
public sealed record EntityAccessRequest(EntityOperation Operation, Type EntityType, object? Entity, ClaimsPrincipal? User);

/// <summary>
/// Decides whether a user may perform an operation on an entity type. Every <see cref="EntityHandler{T}"/>
/// requires one: exposing a type without an explicit access policy is not possible.
/// </summary>
public interface IEntityAccessPolicy
{
    ValueTask<bool> IsAllowedAsync(EntityAccessRequest request, CancellationToken ct = default);
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
    public static IEntityAccessPolicy AllowAll { get; } = From(_ => true);

    /// <summary>Everything is allowed to an authenticated user.</summary>
    public static IEntityAccessPolicy Authenticated { get; } = From(r => r.User?.Identity?.IsAuthenticated == true);

    /// <summary>Read for an authenticated user, no write.</summary>
    public static IEntityAccessPolicy ReadOnly { get; } = From(r =>
        r.Operation == EntityOperation.Read && r.User?.Identity?.IsAuthenticated == true);

    public static IEntityAccessPolicy From(Func<EntityAccessRequest, bool> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new DelegatePolicy((request, _) => ValueTask.FromResult(rule(request)));
    }

    public static IEntityAccessPolicy FromAsync(Func<EntityAccessRequest, CancellationToken, ValueTask<bool>> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new DelegatePolicy(rule);
    }

    private sealed class DelegatePolicy(Func<EntityAccessRequest, CancellationToken, ValueTask<bool>> rule) : IEntityAccessPolicy
    {
        public ValueTask<bool> IsAllowedAsync(EntityAccessRequest request, CancellationToken ct = default) => rule(request, ct);
    }
}

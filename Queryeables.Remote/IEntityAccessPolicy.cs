using System.Security.Claims;

namespace Queryeables.Remote;

/// <summary>Context of a remote call: who is calling.</summary>
/// <param name="User">User authenticated by the server (null when the call is not authenticated).</param>
public sealed record EntityRequestContext(ClaimsPrincipal? User = null)
{
    public static EntityRequestContext Anonymous { get; } = new();

    /// <summary>The exposed types, set by the dispatcher: a row filter through a relation follows the rules of the related type.</summary>
    public IEntityHandlerRegistry? Handlers { get; init; }
}

/// <summary>A read of <paramref name="EntityType"/> by <paramref name="User"/>: what a policy decides on.</summary>
/// <param name="EntityType">The exposed type read.</param>
/// <param name="User">User authenticated by the server (null when the call is not authenticated).</param>
/// <remarks>The rows are not part of it: a row filter (<c>RowLevelSecurity</c>) keeps each user to its own.</remarks>
public sealed record EntityAccessRequest(Type EntityType, ClaimsPrincipal? User);

/// <summary>
/// Decides whether a user may read an entity type. Every <see cref="EntityHandler{T}"/> requires one: exposing a type
/// without an explicit access policy is not possible.
/// </summary>
public interface IEntityAccessPolicy
{
    ValueTask<bool> IsAllowedAsync(EntityAccessRequest request, CancellationToken ct = default);
}

/// <example>
/// <code>
/// // The sales team only:
/// var policy = EntityAccessPolicies.From(r =&gt; r.User?.IsInRole("sales") == true);
/// </code>
/// </example>
public static class EntityAccessPolicies
{
    /// <summary>Every read is allowed, even without authentication. For public data, tests or a closed network.</summary>
    public static IEntityAccessPolicy AllowAll { get; } = From(_ => true);

    /// <summary>Reads are allowed to an authenticated user.</summary>
    public static IEntityAccessPolicy Authenticated { get; } = From(r => r.User?.Identity?.IsAuthenticated == true);

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

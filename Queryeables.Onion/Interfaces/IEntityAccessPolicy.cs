using System.Security.Claims;

namespace Queryeables.Onion;

public enum EntityOperation
{
    Read,
    Insert,
    Update,
    Delete,
}

/// <summary>Contexte d'un appel distant : qui appelle, et depuis quelle connexion de notification.</summary>
/// <param name="User">Utilisateur authentifié par le serveur (null si l'appel n'est pas authentifié).</param>
/// <param name="ConnectionId">Session de notification du client, pour qu'il ignore ses propres changements.</param>
public sealed record EntityRequestContext(ClaimsPrincipal? User = null, string? ConnectionId = null)
{
    public static EntityRequestContext Anonymous { get; } = new();

    public EntityRequestContext WithConnection(string? connectionId) => this with { ConnectionId = connectionId };
}

/// <param name="Entity">L'entité concernée pour une écriture ; null pour une lecture (contrôle au niveau du type).</param>
public sealed record EntityAccessRequest(EntityOperation Operation, Type EntityType, object? Entity, ClaimsPrincipal? User);

/// <summary>
/// Décide si un utilisateur peut faire une opération sur un type d'entité. Chaque <see cref="EntityHandler{T}"/>
/// en exige une : un type exposé sans règle d'accès explicite n'est pas possible.
/// </summary>
public interface IEntityAccessPolicy
{
    ValueTask<bool> IsAllowedAsync(EntityAccessRequest request, CancellationToken ct = default);
}

/// <example>
/// <code>
/// // Lecture pour tout utilisateur connecté, écriture pour le rôle « admin » :
/// var policy = EntityAccessPolicies.From(r =>
///     r.User?.Identity?.IsAuthenticated == true &amp;&amp;
///     (r.Operation == EntityOperation.Read || r.User.IsInRole("admin")));
/// </code>
/// </example>
public static class EntityAccessPolicies
{
    /// <summary>Tout est permis, y compris sans authentification. Pour les tests ou un réseau fermé.</summary>
    public static IEntityAccessPolicy AllowAll { get; } = From(_ => true);

    /// <summary>Tout est permis à un utilisateur authentifié.</summary>
    public static IEntityAccessPolicy Authenticated { get; } = From(r => r.User?.Identity?.IsAuthenticated == true);

    /// <summary>Lecture pour un utilisateur authentifié, aucune écriture.</summary>
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

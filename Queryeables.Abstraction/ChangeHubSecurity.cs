using System.Collections.Concurrent;
using System.Security.Claims;

namespace Queryeables.Abstraction;

/// <summary>What the change hubs (MagicOnion, SignalR) accept from their clients. Register one as a singleton to change it.</summary>
public sealed class ChangeHubOptions
{
    /// <summary>
    /// Relay the changes that clients report (a client with a database of its own, shared with others). Off by default:
    /// the changes then come only from the server's database, and a client cannot make up one (a "delete" that empties
    /// the other caches, a "reload" sent in a loop to every client).
    /// </summary>
    public bool RelayClientChanges { get; init; }

    /// <summary>Changes a connection may report per second, when <see cref="RelayClientChanges"/> is on; beyond, they are dropped.</summary>
    public int MaxClientChangesPerSecond { get; init; } = 20;

    /// <summary>
    /// Changes waiting to be broadcast. When clients cannot keep up, the queue is emptied and one "synchronize" is sent
    /// instead: each cache catches up from the database, nothing is lost and memory stays bounded.
    /// </summary>
    public int QueueCapacity { get; init; } = 10_000;

    /// <summary>Longest key accepted in a change reported by a client.</summary>
    public int MaxKeyLength { get; init; } = 256;
}

/// <summary>
/// Which user each hub connection belongs to. A write names the connection of its author, so that the author does not
/// receive its own change: the server only believes that name when the connection belongs to the caller. Otherwise a
/// client could name another user's connection and keep the change from it.
/// </summary>
/// <remarks>
/// Only a user that can be told apart owns its connections. Anonymous users (and users without a name identifier nor a
/// name) all have the same key: one of them could claim another's connection, so none does, and their changes reach every
/// client, their author included (a cache that reads its own change again).
/// </remarks>
public sealed class ConnectionOwners
{
    private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.Ordinal);

    /// <summary>Records that <paramref name="connectionId"/> belongs to <paramref name="user"/>.</summary>
    public void Register(string connectionId, ClaimsPrincipal? user)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        if (UserKey.Of(user) is { Length: > 0 } owner)
            _owners[connectionId] = owner;
        else
            _owners.TryRemove(connectionId, out _);
    }

    public void Remove(string connectionId)
    {
        if (connectionId is not null)
            _owners.TryRemove(connectionId, out _);
    }

    /// <summary>
    /// <paramref name="connectionId"/> when it belongs to <paramref name="user"/>; null otherwise (unknown, or another
    /// user's), so that the change goes to every client, the caller included.
    /// </summary>
    public string? Verify(string? connectionId, ClaimsPrincipal? user)
        => connectionId is not null && _owners.TryGetValue(connectionId, out var owner)
           && UserKey.Of(user) is { Length: > 0 } caller && owner == caller
            ? connectionId
            : null;
}

/// <summary>A stable key for a user, from its claims.</summary>
public static class UserKey
{
    /// <summary>
    /// The issuer and subject of the user (<see cref="ClaimTypes.NameIdentifier"/>, else its name); an empty key for an
    /// anonymous user; null for an authenticated user with neither, who cannot be told apart from another.
    /// </summary>
    /// <remarks>
    /// Every anonymous user has the same, empty key: it owns no connection (<see cref="ConnectionOwners"/>), and its
    /// operation ids (offline writes) are told apart only by their own randomness: a GUID another anonymous client
    /// cannot guess.
    /// </remarks>
    public static string? Of(ClaimsPrincipal? user)
    {
        if (user?.Identity is not { IsAuthenticated: true } identity)
            return string.Empty;
        if (user.FindFirst(ClaimTypes.NameIdentifier) is { Value.Length: > 0 } subject)
            return $"{subject.Issuer.Length}:{subject.Issuer}:{subject.Value}";
        if (identity.Name is { Length: > 0 } name)
            return $"name:{identity.AuthenticationType}:{name}";
        return null;
    }
}

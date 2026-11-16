using System.Security.Claims;

namespace Queryeables.Abstraction;

/// <summary>Who the change hubs (MagicOnion, SignalR) admit and tell, and how many changes they queue. Register one as a singleton to change it.</summary>
public sealed class ChangeHubOptions
{
    /// <summary>
    /// Accept connections of anonymous users. Off by default: a change names its row (its key, often its content), so an
    /// open hub would tell anyone what moves in the database. Turn it on for public data only; otherwise authenticate the
    /// hub's endpoint, and an anonymous connection is refused.
    /// </summary>
    public bool AllowAnonymous { get; init; }

    /// <summary>
    /// Changes waiting to be broadcast. When clients cannot keep up, the queue is emptied and one "synchronize" is sent
    /// instead: each cache catches up from the database, nothing is lost and memory stays bounded.
    /// </summary>
    public int QueueCapacity { get; init; } = 10_000;

    /// <summary>
    /// Who hears of a change of a row. Without it, every subscriber does: the key of the row and the activity on it reach
    /// users that the row-level security of the services would not let read it. With it, a change reaches only the
    /// subscribers it accepts (a "synchronize everything", which names no row, reaches every one); the rule usually reads
    /// the row and applies the same check as the services' row-level security.
    /// </summary>
    /// <remarks>
    /// Checked after the change, once per subscriber: keep it fast (a lookup by key). A user who could see the row before
    /// the change, but not after, is not told of it, and keeps the row until the next refresh of its cache.
    /// </remarks>
    public Func<ChangeAudience, CancellationToken, ValueTask<bool>>? Visibility { get; init; }
}

/// <summary>A change about to reach a subscriber: <see cref="ChangeHubOptions.Visibility"/> says whether it may.</summary>
/// <param name="User">The user of the subscribed connection; null when anonymous.</param>
public sealed record ChangeAudience(ClaimsPrincipal? User, Type EntityType, string EntityKey, ChangeType ChangeType);

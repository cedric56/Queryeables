using Queryeables.Abstraction;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace Queryeables.Remote;

/// <summary>
/// The results of the writes a server has already applied, by operation id: a write sent again with the same id (its answer
/// was lost, the client retries) gets the first result back instead of being applied twice. This is what makes the offline
/// synchronization safe to repeat: an insert is never created twice.
/// </summary>
/// <remarks>
/// <see cref="MemoryIdempotencyStore"/> is the default: it protects against lost answers and client restarts, not against
/// a restart of the server between the commit of a write and the client's retry. To cover that too, implement this
/// interface over a durable store shared by the servers (a table, Redis...).
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    /// Runs <paramref name="write"/> once for <paramref name="key"/>: a later call with the same key gets the result of the
    /// first successful one, and calls made meanwhile wait for it. A failed write is not kept, so that it can be retried.
    /// </summary>
    Task<TransportResult> RunOnceAsync(string key, Func<Task<TransportResult>> write, CancellationToken ct = default);
}

/// <summary>
/// The results of the last writes, in memory, for <see cref="Retention"/> (a day by default) and at most
/// <see cref="Capacity"/> of them.
/// </summary>
public sealed class MemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly object _trimLock = new();
    private long _sinceTrim;

    public MemoryIdempotencyStore(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>How long a result is kept: longer than a client may stay offline before synchronizing again.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Most results kept; beyond it, the oldest go first.</summary>
    public int Capacity { get; init; } = 100_000;

    public int Count => _entries.Count;

    public async Task<TransportResult> RunOnceAsync(string key, Func<Task<TransportResult>> write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(write);

        var now = _time.GetUtcNow();
        var mine = new Entry(new Lazy<Task<TransportResult>>(write), now);
        var entry = _entries.AddOrUpdate(key, mine, (_, current) => now - current.At > Retention ? mine : current);

        var running = entry.Result.Value;
        TransportResult result;
        try
        {
            result = await running.WaitAsync(ct);
        }
        catch when (running.IsFaulted || running.IsCanceled)
        {
            // The write itself failed. A caller that stopped waiting leaves it running: it may still be applied.
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            throw;
        }

        if (!result.Successful)
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));    // not applied: may be sent again
        else if (ReferenceEquals(entry, mine))
            Trim(now);
        return result;
    }

    private void Trim(DateTimeOffset now)
    {
        // Cleaned in batches, down to 90% of the capacity, and every 1024 results otherwise: never a full scan per write.
        if (_entries.Count <= Capacity && Interlocked.Increment(ref _sinceTrim) % 1024 != 0)
            return;
        lock (_trimLock)
        {
            if (_entries.Count <= Capacity && Volatile.Read(ref _sinceTrim) % 1024 != 0)
                return;
            foreach (var (key, entry) in _entries)
            {
                if (now - entry.At > Retention)
                    _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
            var excess = _entries.Count - (int)(Capacity * 0.9);
            if (excess > 0 && _entries.Count > Capacity)
            {
                foreach (var (key, entry) in _entries.OrderBy(e => e.Value.At).Take(excess).ToList())
                    _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
        }
    }

    private sealed record Entry(Lazy<Task<TransportResult>> Result, DateTimeOffset At);
}

internal static class IdempotencyKey
{
    /// <summary>
    /// The operation, for this user, this type and this kind of write: an id seen under another user is another operation,
    /// and an id sent again for another type or kind is not answered with the first result. The content is not part of
    /// it: a write decided again ("keep mine") is sent with the server's current version, and must still be recognized.
    /// Null when the user cannot be told apart from another (see <see cref="UserKey"/>).
    /// </summary>
    public static string? Of(Guid operationId, ClaimsPrincipal? user, string type, BatchOperationKind kind)
        => UserKey.Of(user) is { } who ? $"{who.Length}:{who}:{type.Length}:{type}:{(int)kind}:{operationId:N}" : null;
}

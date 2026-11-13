using Queryeables.Abstraction;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;
using System.Text;

namespace Queryeables.Remote;

/// <summary>
/// The results of the writes already applied, kept in a table of the database (<see cref="IdempotencyRecord"/>): they
/// survive a restart of the server, and are shared by the servers of a farm. What a server in production uses.
/// </summary>
/// <remarks>
/// <para>
/// A write is marked as started before it runs, and as done with its result after its commit. A server that stops in
/// between leaves it started: the retry of the client then gets a <see cref="EntityErrorCode.Conflict"/> (the write may
/// have been applied, it is not applied twice), which the offline mode shows as a conflict to decide; deciding "keep mine"
/// sends it again, and it is then applied.
/// </para>
/// <para>
/// The table, for SQLite (adapt the types to the database):
/// <code>
/// CREATE TABLE IdempotencyRecords (Id TEXT PRIMARY KEY, Done INTEGER NOT NULL, Result BLOB, At TEXT NOT NULL);
/// </code>
/// </para>
/// </remarks>
public sealed class StoreIdempotencyStore(IEntityStoreFactory stores, TimeProvider? time = null) : IIdempotencyStore
{
    private readonly IEntityStoreFactory _stores = stores ?? throw new ArgumentNullException(nameof(stores));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _writes;

    /// <summary>
    /// How long a result is kept: longer than a client may stay offline (a write sent again after it is applied again).
    /// </summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a call waits for the same write running elsewhere (another request, another server); beyond, a write
    /// still started is taken as interrupted.
    /// </summary>
    public TimeSpan RunningTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How often a call checks whether the same write running elsewhere is done.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    public async Task<TransportResult> RunOnceAsync(string key, Func<Task<TransportResult>> write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(write);
        var id = IdOf(key);

        while (true)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var record = await FindAsync(id, ct);
            if (record is not null && now - record.At > Retention)
            {
                await RemoveAsync(record, ct);              // forgotten: a write this old is taken as new
                record = null;
            }

            if (record is { Done: true })
                return TransportResult.Success(record.Result);

            if (record is not null)
            {
                if (now - record.At < RunningTimeout)
                {
                    await Task.Delay(PollInterval, _time, ct);  // running elsewhere: its result is the answer
                    continue;
                }
                // Started, never finished: the server stopped in between, or the write failed without cleaning up.
                // Not applied again blindly; forgotten, so that the client's decision ("keep mine") can send it again.
                await RemoveAsync(record, ct);
                return TransportResult.Failure(EntityErrorCode.Conflict,
                    "This write was interrupted on the server: it may have been applied. Reload the row before sending it again.");
            }

            var started = new IdempotencyRecord { Id = id, Done = false, At = now };
            if (!await TryInsertAsync(started, ct))
                continue;                                   // started elsewhere at the same time: wait for it
            return await RunAsync(started, write, ct);
        }
    }

    private async Task<TransportResult> RunAsync(IdempotencyRecord started, Func<Task<TransportResult>> write, CancellationToken ct)
    {
        TransportResult result;
        try
        {
            result = await write();
        }
        catch
        {
            await RemoveAsync(started, CancellationToken.None);
            throw;
        }

        if (!result.Successful)
        {
            await RemoveAsync(started, CancellationToken.None);    // not applied: may be sent again
            return result;
        }

        started.Done = true;
        started.Result = result.Data;
        await using (var store = _stores.Create())
            await store.UpdateAsync(started, CancellationToken.None);   // applied: no cancellation may lose this
        if (Interlocked.Increment(ref _writes) % 1024 == 0)
            await TrimAsync(ct);
        return result;
    }

    /// <summary>Deletes the results older than <see cref="Retention"/> (done every 1024 writes; call it from a job otherwise).</summary>
    public async Task<int> TrimAsync(CancellationToken ct = default)
    {
        var before = _time.GetUtcNow().UtcDateTime - Retention;
        await using var store = _stores.Create();
        return await store.DeleteWhereAsync<IdempotencyRecord>(r => r.At < before, ct);
    }

    private async Task<IdempotencyRecord?> FindAsync(string id, CancellationToken ct)
    {
        await using var store = _stores.Create();
        return await store.FirstOrDefaultAsync<IdempotencyRecord>(r => r.Id == id, ct);
    }

    private async Task<bool> TryInsertAsync(IdempotencyRecord record, CancellationToken ct)
    {
        try
        {
            await using var store = _stores.Create();
            await store.InsertAsync(record, ct);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The key is taken (another call won the race), or the database failed: told apart by reading it again.
            if (await FindAsync(record.Id, ct) is not null)
                return false;
            throw;
        }
    }

    private async Task RemoveAsync(IdempotencyRecord record, CancellationToken ct)
    {
        await using var store = _stores.Create();
        await store.DeleteAsync(record, ct);
    }

    /// <summary>A key of fixed length (the keys hold the user's identity, of any length): its SHA-256, in hex.</summary>
    internal static string IdOf(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}

/// <summary>A write a server has started or applied, for <see cref="StoreIdempotencyStore"/>.</summary>
[Table("IdempotencyRecords")]
public sealed class IdempotencyRecord
{
    /// <summary>The SHA-256 of the operation's key (user, type, kind, operation id), in hex.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public string Id { get; set; } = "";

    /// <summary>False while the write runs; true once it is applied.</summary>
    public bool Done { get; set; }

    /// <summary>The serialized result sent back to the client; null when it has none.</summary>
    public byte[]? Result { get; set; }

    /// <summary>When the write started, in UTC (a DateTime: compared the same way by every database).</summary>
    public DateTime At { get; set; }
}

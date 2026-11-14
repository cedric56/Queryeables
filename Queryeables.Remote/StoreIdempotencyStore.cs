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
/// A write is marked as running before it runs, and as done with its result after its commit. While it runs, its server
/// beats (increments <see cref="IdempotencyRecord.Beat"/>): the same write sent meanwhile waits for its result, however
/// long it takes. Only a write whose beat stops for <see cref="RunningTimeout"/>, measured by the clock of the waiting
/// server (the clocks of a farm need not agree), is taken as interrupted: its server stopped. The retry of the client then
/// gets a <see cref="EntityErrorCode.Conflict"/> (the write may have been applied, it is not applied twice), which the
/// offline mode shows as a conflict to decide; deciding "keep mine" sends it again, and it is then applied, unless the
/// interrupted server finished it after all: its result is then the answer.
/// </para>
/// <para>
/// Every change of a record goes through its version (<see cref="IdempotencyRecord.Version"/>): two servers never both
/// take the same write.
/// </para>
/// <para>
/// The table, for SQLite (adapt the types to the database):
/// <code>
/// CREATE TABLE IdempotencyRecords (Id TEXT PRIMARY KEY, State INTEGER NOT NULL, Result BLOB, At TEXT NOT NULL,
///                                  Runner TEXT, Beat INTEGER NOT NULL, Version INTEGER NOT NULL);
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
    /// How long a running write may go without a beat before it is taken as interrupted (its server stopped). Its server
    /// beats four times in that time.
    /// </summary>
    public TimeSpan RunningTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How often a call checks whether the same write running elsewhere is done.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Called when a cleanup or a beat fails (they never fail the write); null to ignore.</summary>
    public Action<Exception>? OnError { get; init; }

    public async Task<TransportResult> RunOnceAsync(string key, Func<Task<TransportResult>> write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(write);
        var id = IdOf(key);
        (long Beat, long SeenAt)? watched = null;          // the beat of a write running elsewhere, and when it last moved

        while (true)
        {
            var record = await FindAsync(id, ct);
            if (record is not null && _time.GetUtcNow().UtcDateTime - record.At > Retention && record.State != IdempotencyState.Running)
            {
                await TryDeleteAsync(record, ct);           // forgotten: a write this old is taken as new
                continue;
            }

            switch (record?.State)
            {
                case null:
                    var started = new IdempotencyRecord
                    {
                        Id = id, State = IdempotencyState.Running, At = _time.GetUtcNow().UtcDateTime, Runner = NewRunner(),
                    };
                    if (await TryInsertAsync(started, ct))
                        return await RunAsync(started, write, ct);
                    continue;                               // started elsewhere at the same time: wait for it

                case IdempotencyState.Done:
                    return TransportResult.Success(record.Result);

                case IdempotencyState.Interrupted:
                    // Sent again after the conflict: the client decided. Taken by one call only (its version).
                    record.State = IdempotencyState.Running;
                    record.Runner = NewRunner();
                    record.Beat = 0;
                    record.At = _time.GetUtcNow().UtcDateTime;
                    if (await TryUpdateAsync(record, ct))
                        return await RunAsync(record, write, ct);
                    continue;

                default:                                    // running elsewhere: its result is the answer
                    var now = _time.GetTimestamp();
                    if (watched is not { } seen || seen.Beat != record.Beat)
                        watched = (record.Beat, now);
                    else if (_time.GetElapsedTime(seen.SeenAt, now) >= RunningTimeout)
                    {
                        // No beat for that long: its server stopped. Marked by one call only (its version); a beat in
                        // between keeps it running.
                        record.State = IdempotencyState.Interrupted;
                        if (await TryUpdateAsync(record, ct))
                            return TransportResult.Failure(EntityErrorCode.Conflict,
                                "This write was interrupted on the server: it may have been applied. Reload the row before sending it again.");
                        watched = null;
                        continue;
                    }
                    await Task.Delay(PollInterval, _time, ct);
                    continue;
            }
        }
    }

    private async Task<TransportResult> RunAsync(IdempotencyRecord running, Func<Task<TransportResult>> write, CancellationToken ct)
    {
        var gate = new SemaphoreSlim(1, 1);                 // a beat and the end never write the record at once
        using var stop = new CancellationTokenSource();
        var beats = BeatAsync(running, gate, stop.Token);
        TransportResult result;
        try
        {
            result = await write();
        }
        catch
        {
            await EndAsync(running, gate, stop, beats, applied: null);
            throw;
        }
        await EndAsync(running, gate, stop, beats, result.Successful ? result : null);

        if (result.Successful && Interlocked.Increment(ref _writes) % 1024 == 0)
            _ = TrimQuietlyAsync();                         // a cleanup never turns a write applied into a failure
        return result;
    }

    /// <summary>Marks <paramref name="running"/> done with <paramref name="applied"/>, or forgets it when nothing was applied.</summary>
    private async Task EndAsync(IdempotencyRecord running, SemaphoreSlim gate, CancellationTokenSource stop, Task beats, TransportResult? applied)
    {
        stop.Cancel();
        await beats;
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            // Nothing may cancel this: an applied write without its result would be applied again.
            for (var record = running; record is not null; record = await FindAsync(running.Id, CancellationToken.None))
            {
                if (applied is null)
                {
                    // Not applied: forgotten, so that it may be sent again; unless another call took it meanwhile.
                    if (record.Runner != running.Runner || record.State == IdempotencyState.Done
                        || await TryDeleteAsync(record, CancellationToken.None))
                        return;
                    continue;
                }
                // Applied. Even when another call took it as interrupted meanwhile (beats lost), the result is recorded:
                // the client's "keep mine" then gets it instead of a second application.
                record.State = IdempotencyState.Done;
                record.Result = applied.Data;
                if (await TryUpdateAsync(record, CancellationToken.None))
                    return;
            }
            if (applied is not null)                        // deleted meanwhile (retention): recorded again
                await TryInsertAsync(new IdempotencyRecord
                {
                    Id = running.Id, State = IdempotencyState.Done, Result = applied.Data, At = _time.GetUtcNow().UtcDateTime,
                }, CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task BeatAsync(IdempotencyRecord running, SemaphoreSlim gate, CancellationToken stop)
    {
        var interval = RunningTimeout / 4;
        try
        {
            while (true)
            {
                await Task.Delay(interval, _time, stop);
                await gate.WaitAsync(stop);
                try
                {
                    running.Beat++;
                    if (!await TryUpdateAsync(running, stop))
                    {
                        // Changed by another call (taken as interrupted): the end reads it again.
                        running.Beat--;
                        return;
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);                            // a lost beat never fails the write
        }
    }

    /// <summary>Deletes the results older than <see cref="Retention"/> (done every 1024 writes; call it from a job otherwise).</summary>
    public async Task<int> TrimAsync(CancellationToken ct = default)
    {
        var before = _time.GetUtcNow().UtcDateTime - Retention;
        await using var store = _stores.Create();
        return await store.DeleteWhereAsync<IdempotencyRecord>(r => r.At < before && r.State != IdempotencyState.Running, ct);
    }

    private async Task TrimQuietlyAsync()
    {
        try
        {
            await TrimAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
        }
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

    /// <summary>Writes <paramref name="record"/> if nobody changed it since it was read (its version); false otherwise.</summary>
    private async Task<bool> TryUpdateAsync(IdempotencyRecord record, CancellationToken ct)
    {
        try
        {
            await using var store = _stores.Create();
            return await store.UpdateAsync(record, ct);
        }
        catch (EntityStoreException ex) when (ex.ErrorCode == EntityErrorCode.Conflict)
        {
            return false;
        }
    }

    private async Task<bool> TryDeleteAsync(IdempotencyRecord record, CancellationToken ct)
    {
        try
        {
            await using var store = _stores.Create();
            return await store.DeleteAsync(record, ct);
        }
        catch (EntityStoreException ex) when (ex.ErrorCode == EntityErrorCode.Conflict)
        {
            return false;
        }
    }

    private static string NewRunner() => Guid.NewGuid().ToString("N");

    /// <summary>A key of fixed length (the keys hold the user's identity, of any length): its SHA-256, in hex.</summary>
    internal static string IdOf(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}

/// <summary>Where a write recorded by <see cref="StoreIdempotencyStore"/> is.</summary>
public enum IdempotencyState
{
    /// <summary>A server is applying it (and beats).</summary>
    Running,

    /// <summary>Applied: <see cref="IdempotencyRecord.Result"/> is the answer.</summary>
    Done,

    /// <summary>Its server stopped while applying it: the next send is answered by applying it.</summary>
    Interrupted,
}

/// <summary>A write a server has started or applied, for <see cref="StoreIdempotencyStore"/>.</summary>
[Table("IdempotencyRecords")]
public sealed class IdempotencyRecord
{
    /// <summary>The SHA-256 of the operation's key (user, type, kind, operation id), in hex.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public string Id { get; set; } = "";

    public IdempotencyState State { get; set; }

    /// <summary>The serialized result sent back to the client; null when it has none.</summary>
    public byte[]? Result { get; set; }

    /// <summary>When the write started, in UTC: for the retention only (the clocks of a farm need not agree).</summary>
    public DateTime At { get; set; }

    /// <summary>Who runs it (a new id each time it is taken): a call that ends never forgets the run of another.</summary>
    public string? Runner { get; set; }

    /// <summary>Incremented by the server running the write, while it runs.</summary>
    public long Beat { get; set; }

    /// <summary>Every change goes through it: two servers never both take or end the same write.</summary>
    [ConcurrencyCheck]
    public long Version { get; set; }
}

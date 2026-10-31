using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables.Remote.Offline;

/// <summary>
/// Lets a remote client keep working without the server, and leaves every switch to the user.
/// <list type="bullet">
/// <item>Online, the stores talk to the server. When a call fails for lack of network, <see cref="StatusChanged"/> reports
/// <see cref="OfflineEvent.ServerUnreachable"/>: the application offers to work offline.</item>
/// <item><see cref="GoOfflineAsync"/>: the stores now read and write a local copy of the offline entities (taken from the
/// server when it answers, kept in <see cref="IOfflineStorage"/>); writes wait in a queue.</item>
/// <item>Offline, the server is checked regularly. When it answers, <see cref="OfflineEvent.ServerAvailable"/> is reported,
/// with the writes waiting and the rows changed on the server (<see cref="OfflineStatus.IsDesynchronized"/>): the
/// application offers to synchronize.</item>
/// <item><see cref="GoOnlineAsync"/>: the queued writes are sent one by one, the local copy is taken again from the server,
/// and the stores talk to the server again. Writes the server refuses become <see cref="Conflicts"/>, resolved by the user
/// with <see cref="KeepMineAsync"/> or <see cref="KeepServerAsync"/>.</item>
/// </list>
/// Nothing switches by itself. Works in WebAssembly: no thread, no blocking wait, storage in the browser
/// (<see cref="BrowserOfflineStorage"/>).
/// </summary>
public sealed class OfflineMode : IAsyncDisposable
{
    private readonly IEntityRemote _remote;
    private readonly IOfflineStorage _storage;
    private readonly DbMonitorRegistry? _registry;
    private readonly IServiceProvider? _services;
    private readonly Dictionary<Type, OfflineEntityType> _types = [];
    private readonly Outbox _outbox = new();
    private readonly List<OfflineConflict> _conflicts = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<OfflineEntityType> _dirty = [];

    private OfflineState _state = new(ConnectionMode.Online, null, -1);
    private bool? _reachable;
    private int? _serverChanges;
    private bool _initialized;
    private bool _conflictsChanged;
    private int _transactionDepth;
    private Task? _probing;

    /// <param name="remote">The transport to the server, the same as the remote stores'.</param>
    /// <param name="serializer">The entity serializer, the same as the server's; it also writes the local copy.</param>
    /// <param name="storage">Where the local copy, the queue and the mode survive a restart.</param>
    /// <param name="registry">Notified of the writes made offline, so that the caches follow the local copy.</param>
    /// <param name="services">Gives the entity graphs (<see cref="IDbTransaction{TEntity}"/>): offline, a parent and its children are saved together too.</param>
    public OfflineMode(IEntityRemote remote, IEntitySerializer serializer, IOfflineStorage storage,
        DbMonitorRegistry? registry = null, IServiceProvider? services = null)
    {
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _registry = registry;
        _services = services;
    }

    internal IEntitySerializer Serializer { get; }

    /// <summary>Prefix of the storage keys: change it when several applications share one storage.</summary>
    public string StoragePrefix { get; init; } = "queryeables-offline";

    /// <summary>How far back a new copy asks again before the latest date received (see <see cref="SyncMetadata.DefaultSyncOverlap"/>).</summary>
    public TimeSpan SyncOverlap { get; init; } = SyncMetadata.DefaultSyncOverlap;

    /// <summary>How often the server is checked while it cannot be used.</summary>
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a check waits for the server.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public ConnectionMode Mode => _state.Mode;

    public OfflineStatus Status => new(_state.Mode, _reachable, _outbox.Count, _conflicts.Count, _state.SnapshotTakenAt, _serverChanges);

    /// <summary>The writes refused during a synchronization, waiting for the user.</summary>
    public IReadOnlyList<OfflineConflict> Conflicts => _conflicts.ToArray();

    /// <summary>
    /// The state changed: tell the user. Raised on the thread that noticed it: a user interface dispatches it to its own
    /// thread.
    /// </summary>
    public event EventHandler<OfflineStatusChangedEventArgs>? StatusChanged;

    // ---------- Configuration ----------

    /// <summary>Makes <typeparamref name="TEntity"/> available offline. Call it before <see cref="InitializeAsync"/>.</summary>
    /// <param name="key">The key. Rows created offline get a temporary key (negative for numbers, a new Guid otherwise),
    /// replaced by the server's when they are sent.</param>
    /// <param name="where">
    /// The rows to copy, for example <c>d => d.OwnerId == session.UserId</c>; every row by default. Its captured values are
    /// read at each download: when they change (another user), the copy is taken again in full.
    /// </param>
    public OfflineMode Add<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TKey>(Expression<Func<TEntity, TKey>> key,
        Expression<Func<TEntity, bool>>? where = null)
        where TEntity : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_initialized)
            throw new InvalidOperationException("Add the offline entity types before InitializeAsync.");
        _types[typeof(TEntity)] = new OfflineEntityType<TEntity, TKey>(this, key, where);
        return this;
    }

    /// <summary>Reads the stored state: the mode, the local copy, the queue. Call it once at startup; later calls do nothing.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;

            _state = OfflineCodec.ReadState(await _storage.ReadAsync(Key("state"), ct));
            await _outbox.LoadAsync(_storage, StoragePrefix, ct);
            _conflicts.AddRange(OfflineCodec.ReadConflicts(await _storage.ReadAsync(Key("conflicts"), ct)));
            foreach (var type in _types.Values)
                await type.LoadAsync(_storage, StoragePrefix, ct);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }

        // Closed while offline: still offline, and watching for the server.
        if (Mode == ConnectionMode.Offline)
            StartProbing();
    }

    // ---------- The user's choices ----------

    /// <summary>
    /// Takes a fresh local copy from the server. Refused while offline writes are waiting: synchronize them first.
    /// </summary>
    public async Task<DbResult> RefreshSnapshotAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            if (_outbox.Count > 0)
                return DbResult.Failure(EntityErrorCode.InvalidRequest,
                    "Offline writes are waiting: synchronize them before taking a new copy.");

            var result = await DownloadAllAsync(ct);
            if (!result.Successful)
                return result;
            await PersistAsync(ct);
        }
        finally
        {
            _gate.Release();
        }

        Raise(OfflineEvent.SnapshotRefreshed);
        return DbResult.Success();
    }

    /// <summary>
    /// Works offline from now on. The local copy is refreshed first when the server answers; without network, the stored
    /// copy is used, and the switch is refused when there is none.
    /// </summary>
    public async Task<DbResult> GoOfflineAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            if (Mode == ConnectionMode.Offline)
                return DbResult.Success();

            if (_outbox.Count == 0)
            {
                var refreshed = await DownloadAllAsync(ct);
                if (!refreshed.Successful && _state.SnapshotTakenAt is null)
                    return DbResult.Failure(EntityErrorCode.Unknown,
                        "No local copy: connect to the server once to download the data before working offline.");
            }

            _state = _state with { Mode = ConnectionMode.Offline };
            await PersistAsync(ct);
        }
        finally
        {
            _gate.Release();
        }

        StartProbing();
        _registry?.PublishResync();      // the caches now read the local copy
        Raise(OfflineEvent.ModeChanged);
        return DbResult.Success();
    }

    /// <summary>
    /// Sends the offline writes, takes a fresh copy, and works online again. Without network, nothing changes. Writes the
    /// server refuses are kept in <see cref="Conflicts"/>; the switch happens anyway.
    /// </summary>
    public async Task<SyncReport> GoOnlineAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        SyncReport report;
        await _gate.WaitAsync(ct);
        try
        {
            if (!await PingAsync(ct))
            {
                SetReachable(false);
                return new SyncReport(false, 0, [], "The server cannot be reached.");
            }

            var sent = 0;
            var refused = new List<OfflineConflict>();
            foreach (var entry in _outbox.Snapshot())
            {
                RemoteResult result;
                try
                {
                    result = await SendAsync(entry, ct);
                }
                catch (Exception ex) when (IsTransportFailure(ex, ct))
                {
                    await PersistAsync(ct);
                    SetReachable(false);
                    return new SyncReport(false, sent, refused, $"The connection was lost during the synchronization: {ex.Message}");
                }

                _outbox.Remove(entry.Id);
                if (result.Successful)
                {
                    sent++;
                }
                else
                {
                    var conflict = new OfflineConflict(entry.Id, entry.Type, entry.Kind, entry.Key, entry.Payload,
                        result.ErrorCode, result.Exception ?? "Refused by the server.", entry.At);
                    _conflicts.Add(conflict);
                    _conflictsChanged = true;
                    refused.Add(conflict);
                }
            }

            var downloaded = await DownloadAllAsync(ct);
            _state = _state with { Mode = ConnectionMode.Online };
            _serverChanges = downloaded.Successful ? 0 : _serverChanges;
            await PersistAsync(ct);
            report = new SyncReport(true, sent, refused, downloaded.Successful ? null : downloaded.Exception);
        }
        finally
        {
            _gate.Release();
        }

        _registry?.PublishResync();      // the caches now read the server
        Raise(OfflineEvent.ModeChanged);
        Raise(OfflineEvent.Synchronized);
        return report;
    }

    /// <summary>Resolves a conflict in favor of the offline write: sent again over the server's current row.</summary>
    public async Task<DbResult> KeepMineAsync(OfflineConflict conflict, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        await _gate.WaitAsync(ct);
        try
        {
            var type = _types.Values.FirstOrDefault(t => t.Name == conflict.Type)
                ?? throw new InvalidOperationException($"{conflict.Type} is not available offline.");

            RemoteResult result;
            try
            {
                result = await type.SendAgainAsync(_remote, conflict, ct);
            }
            catch (Exception ex) when (IsTransportFailure(ex, ct))
            {
                SetReachable(false);
                return DbResult.FromException(ex);
            }

            if (!result.Successful)
                return result.ToDbResult();

            _conflicts.RemoveAll(c => c.Id == conflict.Id);
            _conflictsChanged = true;
            type.MarkLocal(conflict.Key);       // read again from the server, with the version it now has
            await RefreshTypeAsync(type, ct);
            await PersistAsync(ct);
        }
        finally
        {
            _gate.Release();
        }

        Raise(OfflineEvent.Synchronized);
        return DbResult.Success();
    }

    /// <summary>Resolves a conflict in favor of the server: the offline write is dropped.</summary>
    public async Task KeepServerAsync(OfflineConflict conflict, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        await _gate.WaitAsync(ct);
        try
        {
            _conflicts.RemoveAll(c => c.Id == conflict.Id);
            _conflictsChanged = true;
            if (_types.Values.FirstOrDefault(t => t.Name == conflict.Type) is { } type && Mode == ConnectionMode.Online)
            {
                type.MarkLocal(conflict.Key);
                await RefreshTypeAsync(type, ct);
            }
            await PersistAsync(ct);
        }
        finally
        {
            _gate.Release();
        }

        Raise(OfflineEvent.Synchronized);
    }

    /// <summary>The entity of a conflict, as it was written offline: to show it to the user.</summary>
    public TEntity? ReadConflict<TEntity>(OfflineConflict conflict) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(conflict);
        return Serializer.Deserialize<TEntity>(conflict.Payload);
    }

    /// <summary>Asks the server now; updates <see cref="Status"/> and reports a change of reachability.</summary>
    public async Task<bool> CheckServerAsync(CancellationToken ct = default)
    {
        var reachable = await PingAsync(ct);
        if (reachable && Mode == ConnectionMode.Offline && _state.SnapshotTakenAt is { } since)
            _serverChanges = await CountServerChangesAsync(since.UtcDateTime, ct);
        SetReachable(reachable);
        if (!reachable)
            StartProbing();
        return reachable;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_probing is not null)
            await _probing.ContinueWith(static _ => { }, TaskScheduler.Default);
        _stop.Dispose();
        _gate.Dispose();
    }

    // ---------- Used by the stores ----------

    internal OfflineEntityType? TypeOf(Type type) => _types.GetValueOrDefault(type);

    internal OfflineEntityType<TEntity, TKey>? TypeOf<TEntity, TKey>() where TEntity : class where TKey : notnull
        => TypeOf(typeof(TEntity)) as OfflineEntityType<TEntity, TKey>;

    internal OfflineEntityType Require(Type type)
        => TypeOf(type) ?? throw new NotSupportedException(
            $"{type.Name} is not available offline: add it with OfflineMode.Add<{type.Name}, TKey>(...).");

    internal SemaphoreSlim Gate => _gate;

    /// <summary>A key for a row created offline: negative numbers, or a new Guid.</summary>
    internal TKey NewTemporaryKey<TKey>()
    {
        object key;
        if (typeof(TKey) == typeof(Guid))
        {
            key = Guid.NewGuid();
        }
        else
        {
            var next = _state.NextTemporaryKey;
            _state = _state with { NextTemporaryKey = next - 1 };
            key = Type.GetTypeCode(typeof(TKey)) switch
            {
                TypeCode.Int64 => next,
                TypeCode.Int32 => checked((int)next),
                TypeCode.Int16 => checked((short)next),
                _ => throw new NotSupportedException(
                    $"A {typeof(TKey).Name} key cannot be generated offline: give the entity its key before inserting it.")
            };
        }
        return (TKey)key;
    }

    /// <summary>The entity graph of <typeparamref name="TEntity"/>, when one is registered.</summary>
    internal IEntityGraph<TEntity>? GraphOf<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>() where TEntity : class
        => (_services?.GetService(typeof(IDbTransaction<TEntity>)) as IEntityGraphService<TEntity>)?.Graph;

    internal void MarkDirty(OfflineEntityType type) => _dirty.Add(type);

    internal void Notify(OfflineEntityType type, ChangeType change, string key) => _registry?.Publish(change, type.Table, key);

    /// <summary>Queues a write made offline (under the gate), and saves it unless a transaction is open: true when saved.</summary>
    internal async Task<bool> QueueAsync(OfflineEntityType type, BatchOperationKind kind, string key, byte[] payload, CancellationToken ct)
    {
        _outbox.Add(kind, type.Name, key, payload);
        if (_transactionDepth > 0)
            return false;
        await PersistAsync(ct);
        return true;
    }

    internal Dictionary<OfflineEntityType, object> SnapshotTypes() => _types.Values.ToDictionary(t => t, t => t.Snapshot());

    internal void RestoreTypes(Dictionary<OfflineEntityType, object> snapshot)
    {
        foreach (var (type, saved) in snapshot)
        {
            type.Restore(saved);
            MarkDirty(type);
        }
    }

    internal void AfterWrite() => Raise(OfflineEvent.ChangesPending);

    /// <summary>Starts an offline transaction: the copy and the queue are restored unless it commits.</summary>
    internal async Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = (SnapshotTypes(), _outbox.Snapshot(), _state);
            _transactionDepth++;
            return new OfflineTransaction(this, snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A transport failure, as opposed to an error answered by the server.</summary>
    internal static bool IsTransportFailure(Exception exception, CancellationToken ct)
        => exception is not (EntityStoreException or ArgumentException or NotSupportedException or InvalidOperationException)
           && !(exception is OperationCanceledException && ct.IsCancellationRequested);

    /// <summary>An online call failed for lack of network.</summary>
    internal void ReportUnreachable()
    {
        SetReachable(false);
        StartProbing();
    }

    // ---------- Internals ----------

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (!_initialized)
            await InitializeAsync(ct);
    }

    private async Task<RemoteResult> SendAsync(OutboxEntry entry, CancellationToken ct)
    {
        var type = _types.Values.FirstOrDefault(t => t.Name == entry.Type)
            ?? throw new InvalidOperationException($"{entry.Type} is not available offline anymore.");
        var payload = type.PrepareForSend(entry.Payload, entry.Kind);

        var result = entry.Kind switch
        {
            BatchOperationKind.Insert => await _remote.InsertAsync(entry.Type, payload, null, ct),
            BatchOperationKind.Update => await _remote.UpdateAsync(entry.Type, payload, null, ct),
            _ => await _remote.DeleteAsync(entry.Type, payload, null, ct),
        };

        // An update with no row: deleted on the server meanwhile. A delete with no row: already done.
        return entry.Kind == BatchOperationKind.Update && result is { Successful: true, Data: not { Length: > 0 } }
            ? RemoteResult.Failure(EntityErrorCode.NotFound, $"{entry.Type} {entry.Key} was deleted on the server.")
            : result;
    }

    private async Task<DbResult> DownloadAllAsync(CancellationToken ct)
    {
        var before = SnapshotTypes();
        try
        {
            foreach (var type in _types.Values)
            {
                await type.DownloadAsync(_remote, ct);
                MarkDirty(type);
            }
            _state = _state with { SnapshotTakenAt = DateTimeOffset.UtcNow };
            _serverChanges = 0;
            SetReachable(true, raise: false);
            return DbResult.Success();
        }
        catch (Exception ex)
        {
            RestoreTypes(before);     // all the types from one moment, or the previous copy
            if (!IsTransportFailure(ex, ct))
                throw;
            SetReachable(false, raise: false);
            return DbResult.FromException(ex);
        }
    }

    private async Task RefreshTypeAsync(OfflineEntityType type, CancellationToken ct)
    {
        try
        {
            await type.DownloadAsync(_remote, ct);
            MarkDirty(type);
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            SetReachable(false);
        }
    }

    private async Task<int?> CountServerChangesAsync(DateTime since, CancellationToken ct)
    {
        try
        {
            int? total = null;
            foreach (var type in _types.Values)
            {
                if (await type.CountServerChangesAsync(_remote, since, ct) is { } count)
                    total = (total ?? 0) + count;
            }
            return total;
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            return null;
        }
    }

    private async Task<bool> PingAsync(CancellationToken ct)
    {
        var first = _types.Values.FirstOrDefault()
            ?? throw new InvalidOperationException("No entity type is available offline: call OfflineMode.Add<TEntity, TKey>(...).");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await _remote.SelectPagedAsync(first.Name, [], false, 1, 1, timeout.Token);
            return true;    // any answer, even an error, comes from the server
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            return false;
        }
    }

    private void StartProbing()
    {
        if (_probing is { IsCompleted: false } || _stop.IsCancellationRequested)
            return;
        _probing = ProbeAsync(_stop.Token);
    }

    /// <summary>While offline, or while the server does not answer: check it regularly. No thread, only a timer: WebAssembly-friendly.</summary>
    private async Task ProbeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && (Mode == ConnectionMode.Offline || _reachable == false))
        {
            try
            {
                await Task.Delay(ProbeInterval, ct);
                await CheckServerAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // A failed check is a check that says "unreachable": the loop goes on.
            }
        }
    }

    private void SetReachable(bool reachable, bool raise = true)
    {
        var changed = _reachable != reachable;
        _reachable = reachable;
        if (changed && raise)
            Raise(reachable ? OfflineEvent.ServerAvailable : OfflineEvent.ServerUnreachable);
    }

    private void Raise(OfflineEvent reason) => StatusChanged?.Invoke(this, new OfflineStatusChangedEventArgs(reason, Status));

    private string Key(string name) => $"{StoragePrefix}/{name}";

    private async Task PersistAsync(CancellationToken ct)
    {
        foreach (var type in _dirty)
            await type.SaveAsync(_storage, StoragePrefix, ct);
        _dirty.Clear();
        await _outbox.SaveAsync(_storage, StoragePrefix, ct);
        if (_conflictsChanged)
        {
            await _storage.WriteAsync(Key("conflicts"), OfflineCodec.Write(_conflicts), ct);
            _conflictsChanged = false;
        }
        await _storage.WriteAsync(Key("state"), OfflineCodec.Write(_state), ct);
    }

    private sealed class OfflineTransaction(
        OfflineMode mode,
        (Dictionary<OfflineEntityType, object> Types, List<OutboxEntry> Outbox, OfflineState State) snapshot) : IEntityTransaction
    {
        private bool _done;

        public async Task<DbResult> CommitAsync(CancellationToken ct = default)
        {
            if (_done)
                return DbResult.Failure(EntityErrorCode.InvalidRequest, "The transaction is already finished.");
            _done = true;
            await mode._gate.WaitAsync(ct);
            try
            {
                mode._transactionDepth--;
                await mode.PersistAsync(ct);
            }
            finally
            {
                mode._gate.Release();
            }
            mode.AfterWrite();
            return DbResult.Success();
        }

        public async Task<DbResult> RollbackAsync(CancellationToken ct = default)
        {
            if (_done)
                return DbResult.Success();
            _done = true;
            await mode._gate.WaitAsync(ct);
            try
            {
                mode.RestoreTypes(snapshot.Types);
                mode._outbox.Restore(snapshot.Outbox);
                mode._state = snapshot.State with { Mode = mode._state.Mode };
                mode._transactionDepth--;
                await mode.PersistAsync(ct);
            }
            finally
            {
                mode._gate.Release();
            }
            mode._registry?.PublishResync();
            return DbResult.Success();
        }

        public async ValueTask DisposeAsync() => await RollbackAsync();
    }
}

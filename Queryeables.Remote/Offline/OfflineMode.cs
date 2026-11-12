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
public sealed class OfflineMode : IUserScopedData, IAsyncDisposable
{
    private readonly IEntityTransport _remote;
    private readonly IOfflineStorage _storage;
    private readonly ChangeFeedRegistry? _registry;
    private readonly IServiceProvider? _services;
    private readonly Dictionary<Type, OfflineEntityType> _types = [];
    private readonly Outbox _outbox = new();
    private readonly List<OfflineConflict> _conflicts = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Held by an offline transaction from its start to its end, and by the work that must not run in the middle of one
    // (writes outside it, synchronization, conflicts): a rollback never erases another caller's writes, and nothing half
    // done is sent. Always taken before the gate.
    private readonly SemaphoreSlim _exclusive = new(1, 1);
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
    /// <param name="services">Gives the entity graphs (<see cref="IAggregateRepository{TEntity}"/>): offline, a parent and its children are saved together too.</param>
    public OfflineMode(IEntityTransport remote, IEntitySerializer serializer, IOfflineStorage storage,
        ChangeFeedRegistry? registry = null, IServiceProvider? services = null)
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
    public async Task<EntityResult> RefreshSnapshotAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await EnterAsync(ct);
        try
        {
            if (_outbox.Count > 0)
                return EntityResult.Failure(EntityErrorCode.InvalidRequest,
                    "Offline writes are waiting: synchronize them before taking a new copy.");

            var result = await DownloadAllAsync(ct);
            if (!result.Successful)
                return result;
            await PersistAsync(ct);
        }
        finally
        {
            Leave();
        }

        Raise(OfflineEvent.SnapshotRefreshed);
        return EntityResult.Success();
    }

    /// <summary>
    /// Works offline from now on. The local copy is refreshed first when the server answers; without network, the stored
    /// copy is used, and the switch is refused when there is none.
    /// </summary>
    public async Task<EntityResult> GoOfflineAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await EnterAsync(ct);
        try
        {
            if (Mode == ConnectionMode.Offline)
                return EntityResult.Success();

            if (_outbox.Count == 0)
            {
                var refreshed = await DownloadAllAsync(ct);
                if (!refreshed.Successful && _state.SnapshotTakenAt is null)
                    return EntityResult.Failure(EntityErrorCode.Unknown,
                        "No local copy: connect to the server once to download the data before working offline.");
            }

            _state = _state with { Mode = ConnectionMode.Offline };
            await PersistAsync(ct);
        }
        finally
        {
            Leave();
        }

        StartProbing();
        _registry?.PublishReload();      // the caches now read the local copy: everything again
        Raise(OfflineEvent.ModeChanged);
        return EntityResult.Success();
    }

    /// <summary>
    /// Sends the offline writes, takes a fresh copy, and works online again. Without network, nothing changes. Writes the
    /// server refuses are kept in <see cref="Conflicts"/>; the switch happens anyway.
    /// </summary>
    public async Task<SyncReport> GoOnlineAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        SyncReport report;
        using var activity = QueryeablesDiagnostics.ActivitySource.StartActivity("queryeables.offline.sync");
        await EnterAsync(ct);
        try
        {
            if (!await PingAsync(ct))
            {
                SetReachable(false);
                return new SyncReport(false, 0, [], "The server cannot be reached.");
            }

            var sent = 0;
            var refused = new List<OfflineConflict>();
            foreach (var id in _outbox.Snapshot().Select(e => e.Id))
            {
                // The entry as it is now: an earlier insert may have given it the server's key.
                if (_outbox.Find(id) is not { } entry)
                    continue;

                // Recorded as sent before it leaves: from then on, a later write on the row is queued after it instead of
                // changing it, even after a crash (the server may have applied it under its id).
                if (_outbox.MarkSent(entry.Id))
                {
                    await PersistAsync(ct);
                    entry = _outbox.Find(id)!;
                }

                TransportResult result;
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
                    RekeyAfterInsert(entry, result);
                    RemoteDiagnostics.OfflineSent.Add(1, new KeyValuePair<string, object?>(QueryeablesDiagnostics.TypeTag, entry.Type));
                }
                else
                {
                    var conflict = new OfflineConflict(entry.Id, entry.Type, entry.Kind, entry.Key, entry.Payload,
                        result.ErrorCode, result.Exception ?? "Refused by the server.", entry.At);
                    _conflicts.Add(conflict);
                    _conflictsChanged = true;
                    refused.Add(conflict);
                    RemoteDiagnostics.OfflineConflicts.Add(1,
                        new KeyValuePair<string, object?>(QueryeablesDiagnostics.TypeTag, entry.Type),
                        new KeyValuePair<string, object?>(QueryeablesDiagnostics.OutcomeTag, QueryeablesDiagnostics.Outcome(result.ErrorCode)));
                }

                // Acknowledged at once, with its conflict if refused: a crash later in the loop does not send it again (and
                // if the crash comes before, its id makes the new attempt harmless).
                await PersistAsync(ct);
            }

            var downloaded = await DownloadAllAsync(ct);
            _state = _state with { Mode = ConnectionMode.Online };
            _serverChanges = downloaded.Successful ? 0 : _serverChanges;
            await PersistAsync(ct);
            report = new SyncReport(true, sent, refused, downloaded.Successful ? null : downloaded.Exception);
            activity?.SetTag("queryeables.sent", sent);
            activity?.SetTag("queryeables.conflicts", refused.Count);
        }
        finally
        {
            Leave();
        }

        _registry?.PublishReload();      // the caches now read the server: temporary rows of the copy must go
        Raise(OfflineEvent.ModeChanged);
        Raise(OfflineEvent.Synchronized);
        return report;
    }

    /// <summary>Resolves a conflict in favor of the offline write: sent again over the server's current row.</summary>
    public async Task<EntityResult> KeepMineAsync(OfflineConflict conflict, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        await EnterAsync(ct);
        try
        {
            var type = _types.Values.FirstOrDefault(t => t.Name == conflict.Type)
                ?? throw new InvalidOperationException($"{conflict.Type} is not available offline.");

            TransportResult result;
            try
            {
                result = await type.SendAgainAsync(_remote, conflict, ct);
            }
            catch (Exception ex) when (IsTransportFailure(ex, ct))
            {
                SetReachable(false);
                return EntityResult.FromException(ex);
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
            Leave();
        }

        Raise(OfflineEvent.Synchronized);
        return EntityResult.Success();
    }

    /// <summary>Resolves a conflict in favor of the server: the offline write is dropped.</summary>
    public async Task KeepServerAsync(OfflineConflict conflict, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        await EnterAsync(ct);
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
            Leave();
        }

        Raise(OfflineEvent.Synchronized);
    }

    /// <summary>
    /// Drops the offline writes not sent yet and the conflicts, and forgets the local copy (it holds those writes): the stores
    /// talk to the server again, and the next <see cref="GoOfflineAsync"/> takes a new copy. Cannot be undone.
    /// </summary>
    public async Task DiscardPendingChangesAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await ForgetAsync(keepPendingWrites: false, ct);
    }

    /// <summary>True while offline writes wait to be sent: a change of user is refused until they are synchronized or discarded.</summary>
    bool IUserScopedData.HasUnsavedChanges => _outbox.Count > 0;

    /// <summary>
    /// Forgets the local copy, the conflicts and the mode of the previous user (see <see cref="UserScopedData.ChangeUserAsync"/>).
    /// Refused while offline writes wait: synchronize them, or <see cref="DiscardPendingChangesAsync"/>.
    /// </summary>
    public async Task ClearUserDataAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await ForgetAsync(keepPendingWrites: true, ct);
    }

    /// <summary>Can refuse a change of user: <see cref="UserScopedData.ChangeUserAsync"/> asks it first.</summary>
    bool IUserScopedData.CanHoldUnsavedChanges => true;

    /// <param name="keepPendingWrites">
    /// Refuse when writes wait. Checked under the gate, where writes are queued: none can slip in between the check and
    /// the forgetting.
    /// </param>
    private async Task ForgetAsync(bool keepPendingWrites, CancellationToken ct)
    {
        await EnterAsync(ct);
        try
        {
            if (keepPendingWrites && _outbox.Count > 0)
                throw new InvalidOperationException(
                    "Offline writes are waiting: synchronize them (GoOnlineAsync) or discard them (DiscardPendingChangesAsync) before changing user.");

            foreach (var type in _types.Values)
            {
                type.Clear();
                MarkDirty(type);
            }
            _outbox.Restore([]);
            _conflicts.Clear();
            _conflictsChanged = true;
            _state = _state with { Mode = ConnectionMode.Online, SnapshotTakenAt = null };
            _serverChanges = null;
            await PersistAsync(ct);
        }
        finally
        {
            Leave();
        }

        _registry?.PublishReload();      // the caches read the server again
        Raise(OfflineEvent.ModeChanged);
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
        _exclusive.Dispose();
    }

    // ---------- Used by the stores ----------

    internal OfflineEntityType? TypeOf(Type type) => _types.GetValueOrDefault(type);

    internal OfflineEntityType<TEntity, TKey>? TypeOf<TEntity, TKey>() where TEntity : class where TKey : notnull
        => TypeOf(typeof(TEntity)) as OfflineEntityType<TEntity, TKey>;

    internal OfflineEntityType Require(Type type)
        => TypeOf(type) ?? throw new NotSupportedException(
            $"{type.Name} is not available offline: add it with OfflineMode.Add<{type.Name}, TKey>(...).");

    internal SemaphoreSlim Gate => _gate;

    /// <summary>Waits until no offline transaction is open, then takes the gate.</summary>
    internal async Task EnterAsync(CancellationToken ct)
    {
        await _exclusive.WaitAsync(ct);
        try
        {
            await _gate.WaitAsync(ct);
        }
        catch
        {
            _exclusive.Release();
            throw;
        }
    }

    internal void Leave()
    {
        _gate.Release();
        _exclusive.Release();
    }

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
        => (_services?.GetService(typeof(IAggregateRepository<TEntity>)) as IGraphRepository<TEntity>)?.Graph;

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
    /// <remarks>Exclusive: other writes, and a synchronization, wait until it commits or rolls back.</remarks>
    internal async Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct)
    {
        await _exclusive.WaitAsync(ct);
        try
        {
            await _gate.WaitAsync(ct);
        }
        catch
        {
            _exclusive.Release();
            throw;
        }
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

    /// <summary>
    /// An insert under a temporary key got the server's: the writes queued after it on that row (made while its answer was
    /// unknown) take the server's key and version.
    /// </summary>
    private void RekeyAfterInsert(OutboxEntry entry, TransportResult result)
    {
        if (entry.Kind != BatchOperationKind.Insert || result.Data is not { Length: > 0 } inserted
            || _types.Values.FirstOrDefault(t => t.Name == entry.Type) is not { } type
            || type.KeyTextOfData(inserted) is not { } serverKey || serverKey == entry.Key)
            return;
        _outbox.Rekey(entry.Type, entry.Key, serverKey, payload => type.Rebase(payload, inserted));

        // The other writes waiting that refer to that row (an order created offline for a customer created offline) name it
        // by its real key now: sent with the temporary one, they would be refused.
        if (long.TryParse(serverKey, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
            _outbox.Rewrite(queued => _types.Values.FirstOrDefault(t => t.Name == queued.Type)
                ?.RebaseReferences(queued.Payload, type.Type, entry.Key, serverKey));
    }

    /// <summary>The offline types.</summary>
    internal IEnumerable<OfflineEntityType> Types => _types.Values;

    private async Task<TransportResult> SendAsync(OutboxEntry entry, CancellationToken ct)
    {
        var type = _types.Values.FirstOrDefault(t => t.Name == entry.Type)
            ?? throw new InvalidOperationException($"{entry.Type} is not available offline anymore.");
        var payload = type.PrepareForSend(entry.Payload, entry.Kind);

        // The id of the queued write goes with it: sent again after a lost answer or a restart, the server returns the first
        // result instead of applying it twice (an insert would otherwise create a second row).
        var result = await _remote.WriteOnceAsync(entry.Id, entry.Kind, entry.Type, payload, null, ct);

        // An update with no row: deleted on the server meanwhile. A delete with no row: already done.
        return entry.Kind == BatchOperationKind.Update && result is { Successful: true, Data: not { Length: > 0 } }
            ? TransportResult.Failure(EntityErrorCode.NotFound, $"{entry.Type} {entry.Key} was deleted on the server.")
            : result;
    }

    private async Task<EntityResult> DownloadAllAsync(CancellationToken ct)
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
            return EntityResult.Success();
        }
        catch (Exception ex)
        {
            RestoreTypes(before);     // all the types from one moment, or the previous copy
            if (!IsTransportFailure(ex, ct))
                throw;
            SetReachable(false, raise: false);
            return EntityResult.FromException(ex);
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

    // The queue first: a stop after it and before the local copy loses nothing (the write is still sent, and the copy is
    // read again from the server); the other way round, the copy would show a write the queue no longer holds.
    private async Task PersistAsync(CancellationToken ct)
    {
        await _outbox.SaveAsync(_storage, StoragePrefix, ct);
        foreach (var type in _dirty)
            await type.SaveAsync(_storage, StoragePrefix, ct);
        _dirty.Clear();
        if (_conflictsChanged)
        {
            await _storage.WriteAsync(Key("conflicts"), OfflineCodec.Write(_conflicts), ct);
            _conflictsChanged = false;
        }
        await _storage.WriteAsync(Key("state"), OfflineCodec.Write(_state), ct);
    }

    internal sealed class OfflineTransaction(
        OfflineMode mode,
        (Dictionary<OfflineEntityType, object> Types, List<OutboxEntry> Outbox, OfflineState State) snapshot) : IEntityTransaction
    {
        private bool _done;

        /// <summary>Neither committed nor rolled back: its store's writes belong to it.</summary>
        public bool IsOpen => !_done;

        public async Task<EntityResult> CommitAsync(CancellationToken ct = default)
        {
            if (_done)
                return EntityResult.Failure(EntityErrorCode.InvalidRequest, "The transaction is already finished.");
            // Finished only once the gate is taken: a cancelled wait leaves it open, and its disposal rolls it back.
            await mode._gate.WaitAsync(ct);
            if (_done)
            {
                mode._gate.Release();
                return EntityResult.Failure(EntityErrorCode.InvalidRequest, "The transaction is already finished.");
            }
            _done = true;
            try
            {
                mode._transactionDepth--;
                await mode.PersistAsync(CancellationToken.None);
            }
            finally
            {
                mode._gate.Release();
                mode._exclusive.Release();
            }
            mode.AfterWrite();
            return EntityResult.Success();
        }

        public async Task<EntityResult> RollbackAsync(CancellationToken ct = default)
        {
            if (_done)
                return EntityResult.Success();
            await mode._gate.WaitAsync(ct);
            if (_done)
            {
                mode._gate.Release();
                return EntityResult.Success();
            }
            _done = true;
            try
            {
                mode.RestoreTypes(snapshot.Types);
                mode._outbox.Restore(snapshot.Outbox);
                mode._state = snapshot.State with { Mode = mode._state.Mode };
                mode._transactionDepth--;
                await mode.PersistAsync(CancellationToken.None);
            }
            finally
            {
                mode._gate.Release();
                mode._exclusive.Release();
            }
            mode._registry?.PublishReload();
            return EntityResult.Success();
        }

        public async ValueTask DisposeAsync() => await RollbackAsync();
    }
}

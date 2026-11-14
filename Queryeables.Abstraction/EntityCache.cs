using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables.Abstraction;

/// <summary>
/// In-memory cache fed by an <see cref="IEntityStore"/>, whatever the framework (Dommel, EF Core, remote...).
/// Thread-safe: reads never block, reloads are serialized.
/// <para>
/// When an <see cref="IChangeFeed{TEntity}"/> is provided, the cache updates itself on every reported change:
/// row by row when <c>parseKey</c> is provided; otherwise through incremental synchronization when the type has a
/// [LastModified] property; otherwise through a full reload. A burst of notifications triggers only one update
/// per row, or a single reload.
/// </para>
/// <para>
/// After a reconnection, call <see cref="SyncAsync"/>: with [LastModified], only the rows modified
/// during the outage are read again, soft deletes ([SoftDelete]) included.
/// </para>
/// </summary>
/// <example>
/// <code>
/// // Dommel
/// var cache = new EntityCache&lt;Customer, long&gt;(
///     () => DommelEntityStore.Create(SqliteFactory.Instance, connectionString),
///     c => c.Id, change feed, EntityKey.Parse&lt;long&gt;);
///
/// // EF Core, Guid key
/// var cache = new EntityCache&lt;Product, Guid&gt;(
///     () => new EfEntityStore(new AppDbContext(options)),
///     p => p.Id, change feed, EntityKey.Parse&lt;Guid&gt;);
///
/// await cache.LoadAsync();
/// </code>
/// </example>
/// <remarks>
/// Incremental synchronization: idea taken from the Datasync Community Toolkit; coalescing of invalidations:
/// idea taken from ActualLab.Fusion. See CREDITS.md.
/// </remarks>
public sealed class EntityCache<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TKey> : IEntityCache<TEntity, TKey>, IUserScopedData, IDisposable
    where TEntity : class
    where TKey : notnull
{
    private readonly Func<IEntityStore> _storeFactory;
    private readonly Expression<Func<TEntity, TKey>> _keyExpression;
    private readonly Func<TEntity, TKey> _key;
    private readonly Func<string, TKey>? _parseKey;
    private readonly IChangeFeed<TEntity>? _monitor;
    private readonly bool _incremental = SyncMetadata.LastModified(typeof(TEntity)) is not null;

    // One reload at a time; writes to the dictionary go through _writeLock.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>Most rows waiting to be read again; beyond, a change makes the cache synchronize once instead.</summary>
    private const int MaxQueuedRows = 256;
    private readonly object _writeLock = new();
    private volatile ConcurrentDictionary<TKey, TEntity> _byKey = new();
    private volatile bool _disposed;

    // Loaded at least once since the last ClearUserDataAsync: a change of user loads it again.
    private bool _loaded;

    // Timestamp of the most recent row received: starting point of the next synchronization (guarded by _writeLock).
    private DateTime? _syncedUpTo;

    // Entities tracked while a load reads the table (guarded by _writeLock): applied over what it read.
    private List<TEntity>? _trackedDuringRead;

    // Freshness (guarded by _writeLock). _filled: loaded since the last ClearUserDataAsync, unlike _loaded which survives it.
    private bool _filled;
    private DateTimeOffset? _lastRefreshedAt;
    private Exception? _lastError;

    /// <summary>
    /// How far back a synchronization asks again before the latest date received (see
    /// <see cref="SyncMetadata.DefaultSyncOverlap"/>): late commits and clocks that differ between servers.
    /// </summary>
    public TimeSpan SyncOverlap { get; init; } = SyncMetadata.DefaultSyncOverlap;

    // Notification coalescing: one pending full update, and the keys already pending.
    private int _fullRefreshQueued;
    private readonly ConcurrentDictionary<TKey, byte> _queuedKeys = new();

    /// <param name="stores">Creates a store for each load; the cache disposes it afterwards.</param>
    /// <param name="key">Entity key, for example <c>c => c.Id</c>.</param>
    /// <param name="monitor">Optional source of change notifications.</param>
    /// <param name="parseKey">
    /// Reads back the key of a <see cref="EntityChange{T}"/> (<see cref="EntityChange{T}.EntityKey"/>), for row-by-row updates.
    /// Usually <c>EntityKey.Parse&lt;TKey&gt;</c>. The change source must publish the entity key:
    /// with SQLite, the rowid is the key only for an <c>INTEGER PRIMARY KEY</c> table.
    /// When absent, each change triggers a synchronization (or a full reload without [LastModified]).
    /// </param>
    public EntityCache(
        IEntityStoreFactory stores,
        Expression<Func<TEntity, TKey>> key,
        IChangeFeed<TEntity>? monitor = null,
        Func<string, TKey>? parseKey = null)
        : this((stores ?? throw new ArgumentNullException(nameof(stores))).Create, key, monitor, parseKey)
    {
    }

    /// <inheritdoc cref="EntityCache{TEntity, TKey}(IEntityStoreFactory, Expression{Func{TEntity, TKey}}, IChangeFeed{TEntity}?, Func{string, TKey}?)"/>
    public EntityCache(
        Func<IEntityStore> storeFactory,
        Expression<Func<TEntity, TKey>> key,
        IChangeFeed<TEntity>? monitor = null,
        Func<string, TKey>? parseKey = null)
    {
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
        _keyExpression = key ?? throw new ArgumentNullException(nameof(key));
        _key = key.Compile();
        _parseKey = parseKey;
        _monitor = monitor;

        if (_parseKey != null)
            EnsureKeyIsComparable();

        if (_monitor != null)
            _monitor.ChangeReceived += OnChangeReceived;
    }

    /// <summary>Snapshot of the current state: safe to enumerate while the cache updates.</summary>
    public IReadOnlyCollection<TEntity> Entities => _byKey.Values.ToArray();

    public event EventHandler? EntitiesChanged;

    /// <summary>
    /// Raised when an update triggered by the change feed fails (database unavailable, timeout...).
    /// The cache then keeps its previous content.
    /// </summary>
    public event EventHandler<Exception>? RefreshFailed;

    /// <summary>
    /// How much the content can be trusted: <see cref="CacheFreshness.NotLoaded"/> before the first load (and after a change
    /// of user), <see cref="CacheFreshness.Stale"/> when the last read from the source failed (the content is what it was
    /// before), <see cref="CacheFreshness.Current"/> otherwise. A screen can show it ("last updated at...").
    /// </summary>
    public CacheFreshness Freshness
    {
        get
        {
            lock (_writeLock)
                return !_filled ? CacheFreshness.NotLoaded : _lastError is null ? CacheFreshness.Current : CacheFreshness.Stale;
        }
    }

    /// <summary>When the cache last read its source successfully (a load, a synchronization or a row); null before.</summary>
    public DateTimeOffset? LastRefreshedAt
    {
        get
        {
            lock (_writeLock)
                return _lastRefreshedAt;
        }
    }

    /// <summary>Why the last read from the source failed; null once a read succeeds again.</summary>
    public Exception? LastError
    {
        get
        {
            lock (_writeLock)
                return _lastError;
        }
    }

    /// <summary>Reloads the whole cache.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            await Tracked(LoadCoreAsync(ct));
        }
        finally
        {
            _refreshLock.Release();
        }

        RaiseEntitiesChanged();
    }

    /// <summary>Empties the cache: the rows of the previous user are gone. See <see cref="UserScopedData.ChangeUserAsync"/>.</summary>
    public async Task ClearUserDataAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            lock (_writeLock)
            {
                _byKey = new ConcurrentDictionary<TKey, TEntity>();
                _syncedUpTo = null;
                _filled = false;
                _lastRefreshedAt = null;
                _lastError = null;
            }
        }
        finally
        {
            _refreshLock.Release();
        }

        RaiseEntitiesChanged();
    }

    /// <summary>Loads the cache again when it was loaded before the change of user.</summary>
    public Task ReloadUserDataAsync(CancellationToken ct = default) => _loaded ? LoadAsync(ct) : Task.CompletedTask;

    /// <summary>
    /// Updates the cache with what changed since the last synchronization (type with [LastModified]),
    /// or reloads it entirely otherwise. Call it after a reconnection.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            await Tracked(RefreshCoreAsync(ct));
        }
        finally
        {
            _refreshLock.Release();
        }

        RaiseEntitiesChanged();
    }

    public TEntity? Find(TKey key) => _byKey.TryGetValue(key, out var entity) ? entity : null;

    public void Track(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        lock (_writeLock)
        {
            Apply(_byKey, entity);
            _trackedDuringRead?.Add(entity);       // a read in progress may have read the table before this write
        }

        RaiseEntitiesChanged();
    }

    private void Apply(ConcurrentDictionary<TKey, TEntity> byKey, TEntity entity)
    {
        if (SyncMetadata.IsDeleted(entity))
            byKey.TryRemove(_key(entity), out _);
        else
            byKey[_key(entity)] = entity;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_monitor != null)
            _monitor.ChangeReceived -= OnChangeReceived;
    }

    // ---------- Loading and synchronization (called under _refreshLock) ----------

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        using var activity = QueryeablesDiagnostics.StartCacheRefresh(typeof(TEntity), "load");
        lock (_writeLock)
            _trackedDuringRead = [];
        IReadOnlyList<TEntity> entities;
        try
        {
            await using (var store = _storeFactory())
                entities = await store.GetAllAsync<TEntity>(ct);
        }
        catch
        {
            lock (_writeLock)
                _trackedDuringRead = null;
            throw;
        }

        var byKey = new ConcurrentDictionary<TKey, TEntity>();
        DateTime? latest = null;
        foreach (var entity in entities)
        {
            latest = Latest(latest, entity);
            // A store without SyncConventionsDecorator also returns soft-deleted rows.
            if (!SyncMetadata.IsDeleted(entity))
                byKey[_key(entity)] = entity;
        }

        lock (_writeLock)
        {
            // What was tracked while the table was read is newer than what was read: it stays.
            foreach (var tracked in _trackedDuringRead ?? [])
                Apply(byKey, tracked);
            _trackedDuringRead = null;
            _byKey = byKey;
            _syncedUpTo = _incremental ? latest ?? DateTime.MinValue : null;
            _loaded = true;
            _filled = true;
        }
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        using var activity = QueryeablesDiagnostics.StartCacheRefresh(typeof(TEntity), "sync");
        DateTime? since;
        lock (_writeLock)
            since = _syncedUpTo;

        if (!_incremental || since is null)
        {
            await LoadCoreAsync(ct);
            return;
        }

        IReadOnlyList<TEntity> changes;
        lock (_writeLock)
            _trackedDuringRead = [];
        try
        {
            await using var store = _storeFactory();
            changes = await store.SelectChangesAsync<TEntity>(SyncMetadata.Before(since.Value, SyncOverlap), ct);
        }
        catch
        {
            lock (_writeLock)
                _trackedDuringRead = null;
            throw;
        }

        lock (_writeLock)
        {
            var tracked = TakeTracked();
            foreach (var entity in changes)
            {
                if (tracked.Contains(_key(entity)))
                {
                    _syncedUpTo = Latest(_syncedUpTo, entity);
                    continue;                       // written by this application during the read: newer than what was read
                }
                if (SyncMetadata.IsDeleted(entity))
                    _byKey.TryRemove(_key(entity), out _);
                else
                    _byKey[_key(entity)] = entity;

                _syncedUpTo = Latest(_syncedUpTo, entity);
            }
        }
    }

    /// <summary>The keys tracked during the read that ends (under <c>_writeLock</c>); stops tracking them.</summary>
    private HashSet<TKey> TakeTracked()
    {
        var keys = (_trackedDuringRead ?? []).Select(_key).ToHashSet();
        _trackedDuringRead = null;
        return keys;
    }

    private static DateTime? Latest(DateTime? current, TEntity entity)
        => SyncMetadata.LastModifiedUtc(entity) is { } modified && (current is null || modified > current) ? modified : current;

    // ---------- Change feed notifications ----------

    // async void is required by the event signature: no exception may escape.
    private async void OnChangeReceived(object? sender, EntityChange<TEntity> record)
    {
        if (_disposed) return;
        try
        {
            await ApplyAsync(record);
        }
        catch (Exception ex)
        {
            RefreshFailed?.Invoke(this, ex);
        }
    }

    private async Task ApplyAsync(EntityChange<TEntity> record)
    {
        if (record.ChangeType == ChangeType.None)
        {
            // The source changed (offline copy, server, another user): reload, since rows may have vanished.
            if (string.IsNullOrEmpty(record.EntityKey))
                await LoadAsync();
            return;
        }

        // No key, or a key of another kind (a SQLite rowid for a Guid key...): synchronize instead.
        if (_parseKey is null || !TryParseKey(record.EntityKey, out var key))
        {
            await RefreshAllCoalescedAsync();
            return;
        }

        // A burst on many rows: one synchronization rather than a waiting read per row (memory stays bounded).
        if (_queuedKeys.Count >= MaxQueuedRows && !_queuedKeys.ContainsKey(key))
        {
            await RefreshAllCoalescedAsync();
            return;
        }

        // This row is already waiting to be read again: the upcoming read will also see this change.
        if (!_queuedKeys.TryAdd(key, 0))
            return;

        await _refreshLock.WaitAsync();
        using var activity = QueryeablesDiagnostics.StartCacheRefresh(typeof(TEntity), "row");
        try
        {
            // Removed before reading: a change arriving during the read will trigger another read.
            _queuedKeys.TryRemove(key, out _);

            TEntity? entity = null;
            if (record.ChangeType != ChangeType.Delete)
            {
                lock (_writeLock)
                    _trackedDuringRead = [];
                try
                {
                    await using var store = _storeFactory();
                    entity = await Tracked(store.FirstOrDefaultAsync(KeyEquals(key)));
                }
                catch
                {
                    lock (_writeLock)
                        _trackedDuringRead = null;
                    throw;
                }
            }

            lock (_writeLock)
            {
                // Written by this application during the read (its own notification never comes): newer, it stays.
                if (TakeTracked().Contains(key))
                {
                    if (entity is not null && _syncedUpTo is not null)
                        _syncedUpTo = Latest(_syncedUpTo, entity);
                }
                // Row deleted (physically or logically), or not found anymore: remove it.
                else if (entity is null || SyncMetadata.IsDeleted(entity))
                    _byKey.TryRemove(key, out _);
                else
                    _byKey[key] = entity;

                if (entity is not null && _syncedUpTo is not null)
                    _syncedUpTo = Latest(_syncedUpTo, entity);
            }
        }
        finally
        {
            _refreshLock.Release();
        }

        RaiseEntitiesChanged();
    }

    private async Task RefreshAllCoalescedAsync()
    {
        // A full update is already pending: it will cover this change too.
        if (Interlocked.Exchange(ref _fullRefreshQueued, 1) == 1)
            return;

        await _refreshLock.WaitAsync();
        try
        {
            Interlocked.Exchange(ref _fullRefreshQueued, 0);
            await Tracked(RefreshCoreAsync(CancellationToken.None));
        }
        finally
        {
            _refreshLock.Release();
        }

        RaiseEntitiesChanged();
    }

    // ---------- Internals ----------

    /// <summary>A read from the source: its success or failure is the cache's freshness.</summary>
    private async Task Tracked(Task read)
    {
        try
        {
            await read;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_writeLock)
                _lastError = ex;
            throw;
        }
        lock (_writeLock)
        {
            _lastError = null;
            _lastRefreshedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task<T> Tracked<T>(Task<T> read)
    {
        await Tracked((Task)read);
        return read.Result;
    }

    private bool TryParseKey(string? text, out TKey key)
    {
        try
        {
            if (!string.IsNullOrEmpty(text))
            {
                key = _parseKey!(text);
                return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or NotSupportedException
                                       or InvalidCastException)
        {
        }

        key = default!;
        return false;
    }

    private void RaiseEntitiesChanged() => EntitiesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Builds <c>e => e.Id == key</c> from the key expression, translatable by Dommel and EF.</summary>
    private Expression<Func<TEntity, bool>> KeyEquals(TKey key)
    {
        var body = Expression.Equal(_keyExpression.Body, Expression.Constant(key, typeof(TKey)));
        return Expression.Lambda<Func<TEntity, bool>>(body, _keyExpression.Parameters);
    }

    private void EnsureKeyIsComparable()
    {
        try
        {
            _ = KeyEquals(default!);
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException(
                $"Row-by-row updates require a key comparable with == ({typeof(TKey).Name} is not). " +
                "For a composite key, omit parseKey: the cache will synchronize on every change.",
                "parseKey", ex);
        }
    }
}

/// <summary>How much the content of an <see cref="EntityCache{TEntity, TKey}"/> can be trusted.</summary>
public enum CacheFreshness
{
    /// <summary>Never loaded, or forgotten by a change of user.</summary>
    NotLoaded,

    /// <summary>The last read from the source succeeded.</summary>
    Current,

    /// <summary>The last read from the source failed: the content is the one before (see <c>LastError</c>).</summary>
    Stale,
}

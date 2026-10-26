using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables.Abstraction;

/// <summary>
/// In-memory cache fed by an <see cref="IEntityStore"/>, whatever the framework (Dommel, EF Core, remote...).
/// Thread-safe: reads never block, reloads are serialized.
/// <para>
/// When an <see cref="IDbMonitor{TEntity}"/> is provided, the cache updates itself on every reported change:
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
///     c => c.Id, monitor, RecordKey.Parse&lt;long&gt;);
///
/// // EF Core, Guid key
/// var cache = new EntityCache&lt;Product, Guid&gt;(
///     () => new EfEntityStore(new AppDbContext(options)),
///     p => p.Id, monitor, RecordKey.Parse&lt;Guid&gt;);
///
/// await cache.LoadAsync();
/// </code>
/// </example>
/// <remarks>
/// Incremental synchronization: idea taken from the Datasync Community Toolkit; coalescing of invalidations:
/// idea taken from ActualLab.Fusion. See CREDITS.md.
/// </remarks>
public sealed class EntityCache<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TKey> : IEntityCache<TEntity, TKey>, IDisposable
    where TEntity : class
    where TKey : notnull
{
    private readonly Func<IEntityStore> _storeFactory;
    private readonly Expression<Func<TEntity, TKey>> _keyExpression;
    private readonly Func<TEntity, TKey> _key;
    private readonly Func<string, TKey>? _parseKey;
    private readonly IDbMonitor<TEntity>? _monitor;
    private readonly bool _incremental = SyncMetadata.LastModified(typeof(TEntity)) is not null;

    // One reload at a time; writes to the dictionary go through _writeLock.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _writeLock = new();
    private volatile ConcurrentDictionary<TKey, TEntity> _byKey = new();
    private volatile bool _disposed;

    // Timestamp of the most recent row received: starting point of the next synchronization (guarded by _writeLock).
    private DateTime? _syncedUpTo;

    // Notification coalescing: one pending full update, and the keys already pending.
    private int _fullRefreshQueued;
    private readonly ConcurrentDictionary<TKey, byte> _queuedKeys = new();

    /// <param name="storeFactory">Creates a store for each load; the cache disposes it afterwards.</param>
    /// <param name="key">Entity key, for example <c>c => c.Id</c>.</param>
    /// <param name="monitor">Optional source of change notifications.</param>
    /// <param name="parseKey">
    /// Reads back the key of a <see cref="Record{T}"/> (<see cref="Record{T}.EntityKey"/>), for row-by-row updates.
    /// Usually <c>RecordKey.Parse&lt;TKey&gt;</c>. The change source must publish the entity key:
    /// with SQLite, the rowid is the key only for an <c>INTEGER PRIMARY KEY</c> table.
    /// When absent, each change triggers a synchronization (or a full reload without [LastModified]).
    /// </param>
    public EntityCache(
        Func<IEntityStore> storeFactory,
        Expression<Func<TEntity, TKey>> key,
        IDbMonitor<TEntity>? monitor = null,
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
            _monitor.RecordChanged += OnRecordChanged;
    }

    /// <summary>Snapshot of the current state: safe to enumerate while the cache updates.</summary>
    public IReadOnlyCollection<TEntity> Entities => _byKey.Values.ToArray();

    public event EventHandler? EntitiesChanged;

    /// <summary>
    /// Raised when an update triggered by the monitor fails (database unavailable, timeout...).
    /// The cache then keeps its previous content.
    /// </summary>
    public event EventHandler<Exception>? RefreshFailed;

    /// <summary>Reloads the whole cache.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            await LoadCoreAsync(ct);
        }
        finally
        {
            _refreshLock.Release();
        }

        RaiseEntitiesChanged();
    }

    /// <summary>
    /// Updates the cache with what changed since the last synchronization (type with [LastModified]),
    /// or reloads it entirely otherwise. Call it after a reconnection.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            await RefreshCoreAsync(ct);
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
            if (SyncMetadata.IsDeleted(entity))
                _byKey.TryRemove(_key(entity), out _);
            else
                _byKey[_key(entity)] = entity;
        }

        RaiseEntitiesChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_monitor != null)
            _monitor.RecordChanged -= OnRecordChanged;
    }

    // ---------- Loading and synchronization (called under _refreshLock) ----------

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        IReadOnlyList<TEntity> entities;
        await using (var store = _storeFactory())
            entities = await store.GetAllAsync<TEntity>(ct);

        var byKey = new ConcurrentDictionary<TKey, TEntity>();
        DateTime? latest = null;
        foreach (var entity in entities)
        {
            latest = Latest(latest, entity);
            // A store without SyncConventionsStore also returns soft-deleted rows.
            if (!SyncMetadata.IsDeleted(entity))
                byKey[_key(entity)] = entity;
        }

        lock (_writeLock)
        {
            _byKey = byKey;
            _syncedUpTo = _incremental ? latest ?? DateTime.MinValue : null;
        }
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        DateTime? since;
        lock (_writeLock)
            since = _syncedUpTo;

        if (!_incremental || since is null)
        {
            await LoadCoreAsync(ct);
            return;
        }

        IReadOnlyList<TEntity> changes;
        await using (var store = _storeFactory())
            changes = await store.SelectChangesAsync<TEntity>(since.Value, ct);

        lock (_writeLock)
        {
            foreach (var entity in changes)
            {
                if (SyncMetadata.IsDeleted(entity))
                    _byKey.TryRemove(_key(entity), out _);
                else
                    _byKey[_key(entity)] = entity;

                _syncedUpTo = Latest(_syncedUpTo, entity);
            }
        }
    }

    private static DateTime? Latest(DateTime? current, TEntity entity)
        => SyncMetadata.LastModifiedUtc(entity) is { } modified && (current is null || modified > current) ? modified : current;

    // ---------- Monitor notifications ----------

    // async void is required by the event signature: no exception may escape.
    private async void OnRecordChanged(object? sender, Record<TEntity> record)
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

    private async Task ApplyAsync(Record<TEntity> record)
    {
        if (record.ChangeType == ChangeType.None)
            return;

        if (_parseKey is null)
        {
            await RefreshAllCoalescedAsync();
            return;
        }

        var key = _parseKey(record.EntityKey);

        // This row is already waiting to be read again: the upcoming read will also see this change.
        if (!_queuedKeys.TryAdd(key, 0))
            return;

        await _refreshLock.WaitAsync();
        try
        {
            // Removed before reading: a change arriving during the read will trigger another read.
            _queuedKeys.TryRemove(key, out _);

            TEntity? entity = null;
            if (record.ChangeType != ChangeType.Delete)
            {
                await using var store = _storeFactory();
                entity = await store.FirstOrDefaultAsync(KeyEquals(key));
            }

            lock (_writeLock)
            {
                // Row deleted (physically or logically), or not found anymore: remove it.
                if (entity is null || SyncMetadata.IsDeleted(entity))
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
            await RefreshCoreAsync(CancellationToken.None);
        }
        finally
        {
            _refreshLock.Release();
        }

        RaiseEntitiesChanged();
    }

    // ---------- Internals ----------

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

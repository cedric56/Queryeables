using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace Queryeables.Abstraction;

/// <summary>
/// Cache en mémoire alimenté par un <see cref="IEntityStore"/>, quel que soit le framework (Dommel, EF Core, distant...).
/// Thread-safe : les lectures ne bloquent jamais, les rechargements sont sérialisés.
/// <para>
/// Si un <see cref="IDbMonitor{TEntity}"/> est fourni, le cache se met à jour à chaque changement signalé :
/// ligne par ligne quand <c>parseKey</c> est fourni ; sinon par synchronisation incrémentale si le type a une propriété
/// [LastModified] ; sinon par rechargement complet. Une rafale de notifications ne déclenche qu'une mise à jour
/// par ligne, ou qu'un seul rechargement.
/// </para>
/// <para>
/// Après une reconnexion, appelez <see cref="SyncAsync"/> : avec [LastModified], seules les lignes modifiées
/// pendant la coupure sont relues, suppressions logiques ([SoftDelete]) comprises.
/// </para>
/// </summary>
/// <example>
/// <code>
/// // Dommel
/// var cache = new EntityCache&lt;Client, long&gt;(
///     () => DommelEntityStore.Create(SqliteFactory.Instance, connectionString),
///     c => c.Id, monitor, RecordKey.Parse&lt;long&gt;);
///
/// // EF Core, clé Guid
/// var cache = new EntityCache&lt;Produit, Guid&gt;(
///     () => new EfEntityStore(new AppDbContext(options)),
///     p => p.Id, monitor, RecordKey.Parse&lt;Guid&gt;);
///
/// await cache.LoadAsync();
/// </code>
/// </example>
/// <remarks>
/// Synchronisation incrémentale : idée reprise de Datasync Community Toolkit ; regroupement des invalidations :
/// idée reprise d'ActualLab.Fusion. Voir CREDITS.md.
/// </remarks>
public sealed class EntityCache<TEntity, TKey> : IEntityCache<TEntity, TKey>, IDisposable
    where TEntity : class
    where TKey : notnull
{
    private readonly Func<IEntityStore> _storeFactory;
    private readonly Expression<Func<TEntity, TKey>> _keyExpression;
    private readonly Func<TEntity, TKey> _key;
    private readonly Func<string, TKey>? _parseKey;
    private readonly IDbMonitor<TEntity>? _monitor;
    private readonly bool _incremental = SyncMetadata.LastModified(typeof(TEntity)) is not null;

    // Un seul rechargement à la fois ; les écritures sur le dictionnaire passent par _writeLock.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _writeLock = new();
    private volatile ConcurrentDictionary<TKey, TEntity> _byKey = new();
    private volatile bool _disposed;

    // Date de la ligne la plus récente reçue : point de départ de la prochaine synchronisation (protégée par _writeLock).
    private DateTime? _syncedUpTo;

    // Regroupement des notifications : une mise à jour complète en attente, et les clés déjà en attente.
    private int _fullRefreshQueued;
    private readonly ConcurrentDictionary<TKey, byte> _queuedKeys = new();

    /// <param name="storeFactory">Crée un store pour chaque chargement ; le cache le dispose ensuite.</param>
    /// <param name="key">Clé de l'entité, par exemple <c>c => c.Id</c>.</param>
    /// <param name="monitor">Source de notifications de changements, facultative.</param>
    /// <param name="parseKey">
    /// Relit la clé d'un <see cref="Record{T}"/> (<see cref="Record{T}.EntityKey"/>), pour une mise à jour ligne par ligne.
    /// En général <c>RecordKey.Parse&lt;TKey&gt;</c>. La source de changements doit publier la clé de l'entité :
    /// avec SQLite, le rowid n'est la clé que pour une table en <c>INTEGER PRIMARY KEY</c>.
    /// Si absent, chaque changement déclenche une synchronisation (ou un rechargement complet sans [LastModified]).
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

    /// <summary>Copie de l'état actuel : sûre à énumérer pendant que le cache se met à jour.</summary>
    public IReadOnlyCollection<TEntity> Entities => _byKey.Values.ToArray();

    public event EventHandler? EntitiesChanged;

    /// <summary>
    /// Levé quand une mise à jour déclenchée par le moniteur échoue (base indisponible, timeout...).
    /// Le cache garde alors son contenu précédent.
    /// </summary>
    public event EventHandler<Exception>? RefreshFailed;

    /// <summary>Recharge tout le cache.</summary>
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
    /// Met le cache à jour avec ce qui a changé depuis la dernière synchronisation (type avec [LastModified]),
    /// ou le recharge entièrement sinon. À appeler après une reconnexion.
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

    // ---------- Chargement et synchronisation (appelés sous _refreshLock) ----------

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
            // Un store sans SyncConventionsStore renvoie aussi les lignes supprimées logiquement.
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

    // ---------- Notifications du moniteur ----------

    // async void imposé par la signature de l'événement : aucune exception ne doit en sortir.
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

        // Cette ligne est déjà en attente de relecture : la relecture à venir verra aussi ce changement.
        if (!_queuedKeys.TryAdd(key, 0))
            return;

        await _refreshLock.WaitAsync();
        try
        {
            // Retirée avant la lecture : un changement arrivé pendant la lecture déclenchera une nouvelle relecture.
            _queuedKeys.TryRemove(key, out _);

            TEntity? entity = null;
            if (record.ChangeType != ChangeType.Delete)
            {
                await using var store = _storeFactory();
                entity = await store.FirstOrDefaultAsync(KeyEquals(key));
            }

            lock (_writeLock)
            {
                // Ligne supprimée (physiquement ou logiquement), ou introuvable entre-temps : on la retire.
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
        // Une mise à jour complète est déjà en attente : elle couvrira aussi ce changement.
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

    // ---------- Interne ----------

    private void RaiseEntitiesChanged() => EntitiesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Construit <c>e => e.Id == key</c> à partir de l'expression de clé, traduisible par Dommel et EF.</summary>
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
                $"La mise à jour ligne par ligne exige une clé comparable avec == ({typeof(TKey).Name} ne l'est pas). " +
                "Pour une clé composite, omettez parseKey : le cache se synchronisera à chaque changement.",
                "parseKey", ex);
        }
    }
}

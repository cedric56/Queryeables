using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace Queryeables.Abstraction;

/// <summary>
/// Cache en mémoire alimenté par un <see cref="IEntityStore"/>, quel que soit le framework (Dommel, EF Core...).
/// Thread-safe : les lectures ne bloquent jamais, les rechargements sont sérialisés.
/// Si un <see cref="IDbMonitor{TEntity}"/> est fourni, le cache se met à jour à chaque changement signalé :
/// ligne par ligne quand <c>parseKey</c> est fourni, sinon par rechargement complet.
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
public sealed class EntityCache<TEntity, TKey> : IEntityCache<TEntity, TKey>, IDisposable
    where TEntity : class
    where TKey : notnull
{
    private readonly Func<IEntityStore> _storeFactory;
    private readonly Expression<Func<TEntity, TKey>> _keyExpression;
    private readonly Func<TEntity, TKey> _key;
    private readonly Func<string, TKey>? _parseKey;
    private readonly IDbMonitor<TEntity>? _monitor;

    // Un seul rechargement à la fois ; les écritures sur le dictionnaire passent par _writeLock.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _writeLock = new();
    private volatile ConcurrentDictionary<TKey, TEntity> _byKey = new();
    private volatile bool _disposed;

    /// <param name="storeFactory">Crée un store pour chaque chargement ; le cache le dispose ensuite.</param>
    /// <param name="key">Clé de l'entité, par exemple <c>c => c.Id</c>.</param>
    /// <param name="monitor">Source de notifications de changements, facultative.</param>
    /// <param name="parseKey">
    /// Relit la clé d'un <see cref="Record{T}"/> (<see cref="Record{T}.EntityKey"/>), pour une mise à jour ligne par ligne.
    /// En général <c>RecordKey.Parse&lt;TKey&gt;</c>. La source de changements doit publier la clé de l'entité :
    /// avec SQLite, le rowid n'est la clé que pour une table en <c>INTEGER PRIMARY KEY</c>.
    /// Si absent, chaque changement recharge toute la table.
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

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            IReadOnlyList<TEntity> entities;
            await using (var store = _storeFactory())
                entities = await store.GetAllAsync<TEntity>(ct);

            var byKey = new ConcurrentDictionary<TKey, TEntity>();
            foreach (var entity in entities)
                byKey[_key(entity)] = entity;

            lock (_writeLock)
                _byKey = byKey;
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
            _byKey[_key(entity)] = entity;

        RaiseEntitiesChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_monitor != null)
            _monitor.RecordChanged -= OnRecordChanged;
    }

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
            await LoadAsync();
            return;
        }

        var key = _parseKey(record.EntityKey);

        await _refreshLock.WaitAsync();
        try
        {
            TEntity? entity = null;
            if (record.ChangeType != ChangeType.Delete)
            {
                await using var store = _storeFactory();
                entity = await store.FirstOrDefaultAsync(KeyEquals(key));
            }

            lock (_writeLock)
            {
                // Ligne supprimée, ou introuvable entre-temps : on la retire.
                if (entity is null)
                    _byKey.TryRemove(key, out _);
                else
                    _byKey[key] = entity;
            }
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
                "Pour une clé composite, omettez parseKey : le cache se rechargera entièrement à chaque changement.",
                "parseKey", ex);
        }
    }
}

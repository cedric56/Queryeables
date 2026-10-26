using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace Queryeables.Abstraction;

/// <summary>
/// Cache en mémoire alimenté par un <see cref="IEntityStore"/>, quel que soit le framework (Dommel, EF Core...).
/// Thread-safe : les lectures ne bloquent jamais, les rechargements sont sérialisés.
/// Si un <see cref="IDbMonitor{TEntity}"/> est fourni, le cache se met à jour à chaque changement signalé :
/// ligne par ligne quand <c>keyFromRowId</c> est fourni, sinon par rechargement complet.
/// </summary>
/// <example>
/// <code>
/// // Dommel
/// var cache = new EntityCache&lt;Client, long&gt;(
///     () => DommelEntityStore.Create(SqliteFactory.Instance, connectionString),
///     c => c.Id, monitor, rowId => rowId);
///
/// // EF Core
/// var cache = new EntityCache&lt;Client, long&gt;(
///     () => new EfEntityStore(new AppDbContext(options)),
///     c => c.Id, monitor, rowId => rowId);
///
/// await cache.LoadAsync();
/// </code>
/// </example>
public abstract class InMemoryEntityCache<TEntity, TKey> : IEntityCache<TEntity, TKey>, IDisposable
    where TEntity : class
    where TKey : notnull
{
    protected readonly Func<IEntityStore> _storeFactory;
    private readonly Expression<Func<TEntity, TKey>> _keyExpression;
    protected readonly Func<TEntity, TKey> _key;
    private readonly Func<long, TKey>? _keyFromRowId;
    private readonly IDbMonitor<TEntity>? _monitor;

    // Un seul rechargement à la fois ; les écritures sur le dictionnaire passent par _writeLock.
    protected readonly SemaphoreSlim _refreshLock = new(1, 1);
    protected readonly object _writeLock = new();
    protected volatile ConcurrentDictionary<TKey, TEntity> _byKey = new();
    private volatile bool _disposed;

    /// <param name="storeFactory">Crée un store pour chaque chargement ; le cache le dispose ensuite.</param>
    /// <param name="key">Clé de l'entité, par exemple <c>c => c.Id</c>.</param>
    /// <param name="monitor">Source de notifications de changements, facultative.</param>
    /// <param name="keyFromRowId">
    /// Convertit le RowId d'un <see cref="Record{T}"/> en clé, pour une mise à jour ligne par ligne.
    /// Pour une table SQLite avec <c>INTEGER PRIMARY KEY</c> : <c>rowId => rowId</c>.
    /// Si absent, chaque changement recharge toute la table.
    /// </param>
    public InMemoryEntityCache(
        Func<IEntityStore> storeFactory,
        Expression<Func<TEntity, TKey>> key,
        IDbMonitor<TEntity>? monitor = null,
        Func<long, TKey>? keyFromRowId = null)
    {
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
        _keyExpression = key ?? throw new ArgumentNullException(nameof(key));
        _key = key.Compile();
        _keyFromRowId = keyFromRowId;
        _monitor = monitor;

        if (_keyFromRowId != null)
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

    public abstract Task LoadAsync(CancellationToken ct = default);

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

        if (_keyFromRowId is null)
        {
            await LoadAsync();
            return;
        }

        var key = _keyFromRowId(record.RowId);

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

    protected void RaiseEntitiesChanged() => EntitiesChanged?.Invoke(this, EventArgs.Empty);

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
                "Pour une clé composite, omettez keyFromRowId : le cache se rechargera entièrement à chaque changement.",
                "keyFromRowId", ex);
        }
    }
}
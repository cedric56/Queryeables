using System.Data.Common;

namespace Queryeables.Abstraction;

public abstract class InMemoryEntityCache<TEntity, TKey> : IEntityCache<TEntity, TKey>, IDisposable 
    where TEntity : class, new()
    where TKey : notnull
{
    public event EventHandler<EventArgs>? EntitiesChanged;

    protected Dictionary<TKey, TEntity> _byId = [];
    protected readonly Func<TEntity, TKey> _id;
    protected readonly IDBMonitor<TEntity>? _monitor;
    protected readonly DbProviderFactory? _factory;
    public IEnumerable<TEntity> Entities => _byId.Select(b => b.Value);

    IReadOnlyCollection<TEntity> IEntityCache<TEntity, TKey>.Entities => throw new NotImplementedException();

    public InMemoryEntityCache(Func<TEntity, TKey> id, DbProviderFactory? factory, IDBMonitor<TEntity>? monitor = null)
    {
        _id = id;
        _factory = factory;
        _monitor = monitor;
        _monitor?.OnRecordChanged += Monitor_OnChanged;
    }

    public InMemoryEntityCache(Func<TEntity, TKey> id, IDBMonitor<TEntity>? monitor = null)
        : this(id, null, monitor)
    {

    }

    event EventHandler? IEntityCache<TEntity, TKey>.EntitiesChanged
    {
        add
        {
            throw new NotImplementedException();
        }

        remove
        {
            throw new NotImplementedException();
        }
    }

    public void Track(TEntity entity) => _byId[_id(entity)] = entity;

    private async void Monitor_OnChanged(object? sender, Record<TEntity> e)
    {
        await ClearEntities();
    }

    public async Task ClearEntities()
    {
        await LoadAsync();
        EntitiesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _monitor?.OnRecordChanged -= Monitor_OnChanged;
    }

    public abstract Task LoadAsync(CancellationToken ct = default);

    public TEntity? Find(TKey key)
    {
        return _byId.TryGetValue(key, out var e) ? e : default;
    }
}

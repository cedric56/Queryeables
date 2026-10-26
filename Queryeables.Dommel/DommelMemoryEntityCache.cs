using Dommel;
using Queryeables.Abstraction;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Linq.Expressions;

namespace Queryeables.Dommel;

public class DommelMemoryEntityCache<TEntity, TKey> : InMemoryEntityCache<TEntity, TKey>, IDisposable
where TEntity : class, new()
where TKey : notnull
{
    public DommelMemoryEntityCache(Func<IEntityStore> storeFactory, Expression<Func<TEntity, TKey>> key, IDbMonitor<TEntity>? monitor = null, Func<long, TKey>? keyFromRowId = null) : base(storeFactory, key, monitor, keyFromRowId)
    {
    }

    public override async Task LoadAsync(CancellationToken ct = default)
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
}

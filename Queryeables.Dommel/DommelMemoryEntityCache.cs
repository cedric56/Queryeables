using Dommel;
using Queryeables.Abstraction;
using System.Data.Common;

namespace Queryeables.Dommel;

public class DommelMemoryEntityCache<TEntity, TKey> : InMemoryEntityCache<TEntity, TKey>, IDisposable
where TEntity : class, new()
where TKey : notnull
{
    protected DommelMemoryEntityCache(Func<TEntity, TKey> id, DbProviderFactory? factory, IDBMonitor<TEntity>? monitor = null) : base(id, factory, monitor)
    {
    }

    public override async Task LoadAsync(CancellationToken ct = default)
    {
        if (_factory == null)
            return;

        using var connection = _factory.CreateConnection()!;
        var entities = await connection.GetAllAsync<TEntity>();
        _byId = entities.ToDictionary(p => _id(p), p => p);
    }
}

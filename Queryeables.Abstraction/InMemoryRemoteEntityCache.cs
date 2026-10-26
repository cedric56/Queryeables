using Avae.DAL;
using Avae.Onion;

namespace Example.Models;

public sealed class InMemoryRemoteEntityCache<T> : InMemoryEntityCache<T> where T : class, new()
{
    MagicOnionLayer magicOnionLayer;
    public InMemoryRemoteEntityCache(MagicOnionLayer magicOnionLayer, Func<T, long> id, IDBMonitor<T>? monitor = null)
        : base(id, monitor)
    {
        this.magicOnionLayer = magicOnionLayer;
    }

    public override async Task LoadEntities()
    {
        var entities = await magicOnionLayer.GetAllAsync<T>();
        _byId = entities.ToDictionary(p => _id(p), p => p);
    }
}

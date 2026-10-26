using System.Collections.Concurrent;

namespace Queryeables.Onion;

public sealed class EntityHandlerRegistry : IEntityHandlerRegistry
{
    private readonly ConcurrentDictionary<string, EntityHandler> _handlers;

    public EntityHandlerRegistry(IDictionary<string, EntityHandler>? handlers = null)
        => _handlers = new ConcurrentDictionary<string, EntityHandler>(handlers ?? new Dictionary<string, EntityHandler>());

    public bool TryGet(string name, out EntityHandler handler)
        => _handlers.TryGetValue(name, out handler!);

    public void Register(string name, EntityHandler handler)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[name] = handler;
    }

    /// <summary>Enregistre le gestionnaire sous le nom de son type, celui qu'utilise <see cref="MagicOnionEntityStore"/>.</summary>
    public void Register(EntityHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register(handler.Type.Name, handler);
    }
}

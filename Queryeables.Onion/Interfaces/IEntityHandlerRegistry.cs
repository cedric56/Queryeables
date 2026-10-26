namespace Queryeables.Onion;

public interface IEntityHandlerRegistry
{
    bool TryGet(string name, out EntityHandler handler);
    void Register(string name, EntityHandler handler);
}

public sealed class EntityHandlerRegistry(Dictionary<string, EntityHandler>? handlers = null) : IEntityHandlerRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, EntityHandler> _handlers = handlers ?? new();

    public bool TryGet(string name, out EntityHandler handler)
    {
        lock (_lock)
        {
            return _handlers.TryGetValue(name, out handler!);
        }
    }

    public void Register(string name, EntityHandler handler)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_lock)
        {
            _handlers[name] = handler;
        }
    }
}
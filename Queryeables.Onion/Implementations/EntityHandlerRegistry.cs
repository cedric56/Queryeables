using System.Collections.Concurrent;

namespace Queryeables.Onion;

public sealed class EntityHandlerRegistry : IEntityHandlerRegistry
{
    private readonly ConcurrentDictionary<string, EntityHandler> _handlers = new();

    public EntityHandlerRegistry(IDictionary<string, EntityHandler>? handlers = null)
    {
        foreach (var (name, handler) in handlers ?? new Dictionary<string, EntityHandler>())
            Register(name, handler);
    }

    public bool TryGet(string name, out EntityHandler handler)
        => _handlers.TryGetValue(name, out handler!);

    /// <summary>
    /// Registers a handler under a name. Replacing the handler of the same type is allowed;
    /// reusing the name of another type throws, instead of silently exposing the wrong table.
    /// </summary>
    public void Register(string name, EntityHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);

        _handlers.AddOrUpdate(name, handler, (_, existing) =>
            existing.Type == handler.Type
                ? handler
                : throw new InvalidOperationException(
                    $"The name '{name}' is already used by {existing.Type.FullName}. " +
                    $"Give {handler.Type.FullName} a distinct name with [RemoteName]."));
    }

    /// <summary>Registers the handler under its type name, the one used by <see cref="MagicOnionEntityStore"/>.</summary>
    public void Register(EntityHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register(EntityName.Of(handler.Type), handler);
    }
}

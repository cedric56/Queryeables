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
    /// Enregistre un gestionnaire sous un nom. Remplacer le gestionnaire d'un même type est permis ;
    /// réutiliser le nom d'un autre type lève une exception, au lieu d'exposer silencieusement la mauvaise table.
    /// </summary>
    public void Register(string name, EntityHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);

        _handlers.AddOrUpdate(name, handler, (_, existing) =>
            existing.Type == handler.Type
                ? handler
                : throw new InvalidOperationException(
                    $"Le nom « {name} » est déjà utilisé par {existing.Type.FullName}. " +
                    $"Donnez un nom distinct à {handler.Type.FullName} avec [RemoteName]."));
    }

    /// <summary>Enregistre le gestionnaire sous le nom de son type, celui qu'utilise <see cref="MagicOnionEntityStore"/>.</summary>
    public void Register(EntityHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register(EntityName.Of(handler.Type), handler);
    }
}

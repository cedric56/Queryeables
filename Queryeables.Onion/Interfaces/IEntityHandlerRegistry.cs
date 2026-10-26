namespace Queryeables.Onion;

/// <summary>Entity types exposed by the server. A type that is not registered cannot be reached remotely.</summary>
public interface IEntityHandlerRegistry
{
    bool TryGet(string name, out EntityHandler handler);
    void Register(string name, EntityHandler handler);
}

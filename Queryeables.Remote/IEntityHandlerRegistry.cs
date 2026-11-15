namespace Queryeables.Remote;

/// <summary>Entity types exposed by the server. A type that is not registered cannot be reached remotely.</summary>
public interface IEntityHandlerRegistry
{
    bool TryGet(string name, out EntityHandler handler);
    void Register(string name, EntityHandler handler);

    /// <summary>The handlers registered, for the checks of the configuration at startup; empty when not known.</summary>
    IReadOnlyCollection<EntityHandler> Handlers => [];
}

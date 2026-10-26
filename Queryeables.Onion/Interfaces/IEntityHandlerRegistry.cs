namespace Queryeables.Onion;

/// <summary>Types d'entités exposés par le serveur. Un type non enregistré est inaccessible à distance.</summary>
public interface IEntityHandlerRegistry
{
    bool TryGet(string name, out EntityHandler handler);
    void Register(string name, EntityHandler handler);
}

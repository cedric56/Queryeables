namespace Queryeables.Abstraction;

/// <summary>
/// Creates the <see cref="IEntityStore"/> of each unit of work. Register the factory, never a store: a store is one
/// connection (or one <c>DbContext</c>) that its user disposes, while caches and services live for the whole application.
/// </summary>
/// <example>
/// <code>
/// IEntityStoreFactory stores = new EntityStoreFactory(() => DommelEntityStore.Create(factory));
/// await using var store = stores.Create();
/// </code>
/// </example>
public interface IEntityStoreFactory
{
    /// <summary>A new store; the caller disposes it.</summary>
    IEntityStore Create();

    /// <summary>
    /// True when the stores talk to a remote server. The server then saves an entity with its children
    /// (<see cref="EntityGraphService{TParent, TKey}"/> sends the parent only, children included).
    /// </summary>
    bool IsRemote => false;
}

/// <summary><see cref="IEntityStoreFactory"/> over a delegate.</summary>
public sealed class EntityStoreFactory(Func<IEntityStore> create, bool isRemote = false) : IEntityStoreFactory
{
    private readonly Func<IEntityStore> _create = create ?? throw new ArgumentNullException(nameof(create));

    public IEntityStore Create() => _create();

    public bool IsRemote { get; } = isRemote;
}

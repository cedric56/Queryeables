using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables.Remote.Offline;

/// <summary>What <c>WithOfflineMode</c> configures: the storage, the entity types available offline, the checks.</summary>
public sealed class OfflineModeOptions
{
    private readonly List<Action<OfflineMode>> _entities = [];
    private IOfflineStorage? _storage;

    /// <inheritdoc cref="OfflineMode.ProbeInterval"/>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc cref="OfflineMode.ProbeTimeout"/>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc cref="OfflineMode.SyncOverlap"/>
    public TimeSpan SyncOverlap { get; set; } = SyncMetadata.DefaultSyncOverlap;

    /// <inheritdoc cref="OfflineMode.StoragePrefix"/>
    public string StoragePrefix { get; set; } = "queryeables-offline";

    /// <summary>
    /// Where the local copy and the queue survive a restart: <see cref="BrowserOfflineStorage"/> in WebAssembly,
    /// <see cref="FileOfflineStorage"/> elsewhere. Without it, in memory: lost when the application closes.
    /// </summary>
    public OfflineModeOptions UseStorage(IOfflineStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        return this;
    }

    /// <summary>Makes <typeparamref name="TEntity"/> available offline. Add the children of a graph too.</summary>
    /// <inheritdoc cref="OfflineMode.Add{TEntity, TKey}(Expression{Func{TEntity, TKey}}, Expression{Func{TEntity, bool}}?)" path="/param"/>
    public OfflineModeOptions Entity<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TKey>(Expression<Func<TEntity, TKey>> key,
        Expression<Func<TEntity, bool>>? where = null)
        where TEntity : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(key);
        _entities.Add(mode => mode.Add(key, where));
        return this;
    }

    /// <summary>The controller these options describe.</summary>
    public OfflineMode Create(IEntityRemote remote, IEntitySerializer serializer, DbMonitorRegistry? registry = null,
        IServiceProvider? services = null)
    {
        var mode = new OfflineMode(remote, serializer, _storage ?? new MemoryOfflineStorage(), registry, services)
        {
            ProbeInterval = ProbeInterval,
            ProbeTimeout = ProbeTimeout,
            StoragePrefix = StoragePrefix,
            SyncOverlap = SyncOverlap,
        };
        foreach (var add in _entities)
            add(mode);
        return mode;
    }
}

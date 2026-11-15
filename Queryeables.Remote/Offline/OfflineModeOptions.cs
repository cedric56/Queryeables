using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables.Remote.Offline;

/// <summary>What <c>WithOfflineMode</c> configures: the storage, the entity types available offline, the checks.</summary>
public sealed class OfflineModeOptions
{
    private readonly List<Action<OfflineMode>> _entities = [];
    private readonly Dictionary<Type, LambdaExpression?> _declared = [];
    private bool _allGenerated;
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

    /// <summary>
    /// Makes <typeparamref name="TEntity"/> available offline, with the key the generator knows (<c>AddGeneratedEntities()</c>):
    /// no key to repeat.
    /// </summary>
    /// <param name="where">The rows copied (see <see cref="OfflineMode.Add{TEntity, TKey}"/>); every row the server gives without it.</param>
    public OfflineModeOptions Entity<TEntity>(Expression<Func<TEntity, bool>>? where = null) where TEntity : class
    {
        _declared[typeof(TEntity)] = where;
        return this;
    }

    /// <summary>
    /// Makes every entity type the generator knows available offline (<c>AddGeneratedEntities()</c>: caches, graphs and their
    /// children, references), each with all the rows the server gives; <see cref="Entity{TEntity}"/> or
    /// <see cref="Entity{TEntity, TKey}"/> still set a filter on one of them.
    /// </summary>
    public OfflineModeOptions AllGeneratedEntities()
    {
        _allGenerated = true;
        return this;
    }

    /// <summary>Adds the generated entity types asked for, with their keys.</summary>
    private sealed class GeneratedTypes(OfflineMode mode, IReadOnlyDictionary<Type, LambdaExpression?> declared) : IEntityTypeVisitor
    {
        public void Visit<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity,
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TKey>(EntityType<TEntity, TKey> type)
            where TEntity : class
            where TKey : notnull
            => mode.Add(type.Key, declared.GetValueOrDefault(typeof(TEntity)) as Expression<Func<TEntity, bool>>);
    }

    /// <summary>The controller these options describe.</summary>
    public OfflineMode Create(IEntityTransport remote, IEntitySerializer serializer, ChangeFeedRegistry? registry = null,
        IServiceProvider? services = null)
    {
        var mode = new OfflineMode(remote, serializer, _storage ?? new MemoryOfflineStorage(), registry, services)
        {
            ProbeInterval = ProbeInterval,
            ProbeTimeout = ProbeTimeout,
            StoragePrefix = StoragePrefix,
            SyncOverlap = SyncOverlap,
        };
        if (_allGenerated || _declared.Count > 0)
        {
            var generated = (services?.GetService(typeof(IEnumerable<IEntityType>)) as IEnumerable<IEntityType> ?? []).ToList();
            var missing = _declared.Keys.Where(t => generated.All(g => g.Type != t)).Select(t => t.Name).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"{string.Join(", ", missing)}: no key known for offline use. Call AddGeneratedEntities() before WithOfflineMode, " +
                    "or give the key: Entity<TEntity, TKey>(e => e.Id).");
            var visitor = new GeneratedTypes(mode, _declared);
            foreach (var type in generated.Where(g => _allGenerated || _declared.ContainsKey(g.Type)))
                type.Accept(visitor);
        }
        foreach (var add in _entities)
            add(mode);                          // given with their keys: these win
        return mode;
    }
}

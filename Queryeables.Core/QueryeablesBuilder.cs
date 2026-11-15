using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Queryeables.Abstraction;
using Queryeables.Remote;
using Queryeables.Remote.Offline;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables;

/// <summary>
/// Configures Queryeables inside <see cref="QueryeablesServiceCollectionExtensions.UseQueryeables"/>: where the stores come from
/// (a provider: <c>UseSqlite</c>, <c>UseDommel</c>, <c>UseEntityFramework</c>, <c>UseMagicOnion</c>...), then the entities
/// (<c>AddGeneratedEntities()</c>, or <see cref="AddCache{TEntity, TKey}"/> and <see cref="AddGraph{TParent, TKey}"/> by hand).
/// </summary>
public sealed class QueryeablesBuilder
{
    private Func<IServiceProvider, IEntityStoreFactory>? _stores;
    private bool _syncConventions;
    private OfflineModeOptions? _offline;
    private readonly List<IEntityType> _entities = [];

    internal QueryeablesBuilder(IServiceCollection services)
    {
        Services = services;
        Services.TryAddSingleton(Options);
    }

    public IServiceCollection Services { get; }

    /// <summary>
    /// Where the stores come from. Providers call it; call it yourself for a custom <see cref="IEntityStore"/>.
    /// The last call wins.
    /// </summary>
    public QueryeablesBuilder UseStores(Func<IServiceProvider, IEntityStoreFactory> stores)
    {
        _stores = stores ?? throw new ArgumentNullException(nameof(stores));
        return this;
    }

    /// <inheritdoc cref="UseStores(Func{IServiceProvider, IEntityStoreFactory})"/>
    /// <param name="create">Creates one store; its user disposes it.</param>
    /// <param name="isRemote">True for stores that talk to a remote server (see <see cref="IEntityStoreFactory.IsRemote"/>).</param>
    public QueryeablesBuilder UseStores(Func<IServiceProvider, IEntityStore> create, bool isRemote = false)
    {
        ArgumentNullException.ThrowIfNull(create);
        return UseStores(sp => new EntityStoreFactory(() => create(sp), isRemote));
    }

    /// <summary>
    /// Applies [LastModified] and [SoftDelete] to every store (see <see cref="SyncConventionsDecorator"/>). On a remote client,
    /// apply it on the server instead.
    /// </summary>
    public QueryeablesBuilder WithSyncConventions()
    {
        _syncConventions = true;
        return this;
    }

    /// <summary>
    /// Lets a remote client work without the server (<see cref="OfflineMode"/>, registered as a singleton): the user is told
    /// when the server is lost or back and when the data differs, and chooses when to switch. Call
    /// <see cref="OfflineMode.InitializeAsync"/> once at startup, before the first store is created.
    /// </summary>
    /// <example>
    /// <code>
    /// q.UseMagicOnion(url).AddGeneratedEntities()
    ///  .WithOfflineMode(o => o
    ///      .UseStorage(new IndexedDbOfflineStorage())     // WebAssembly; SqliteOfflineStorage elsewhere
    ///      .AllGeneratedEntities());
    /// </code>
    /// </example>
    public QueryeablesBuilder WithOfflineMode(Action<OfflineModeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = _offline ?? new OfflineModeOptions();
        configure(options);
        if (_offline is null)
        {
            _offline = options;
            Services.TryAddSingleton(sp => options.Create(
                sp.GetRequiredService<IEntityTransport>(),
                sp.GetRequiredService<IEntitySerializer>(),
                sp.GetService<ChangeFeedRegistry>(),
                sp));
            Services.AddSingleton<IUserScopedData>(sp => sp.GetRequiredService<OfflineMode>());
        }
        return this;
    }

    /// <summary>
    /// Registers the change feed of <typeparamref name="TEntity"/> (<see cref="IChangeFeed{TEntity}"/>), added to
    /// <see cref="ChangeFeedRegistry"/> so that the changes of the database reach it.
    /// </summary>
    /// <param name="table">Name of the table; by default the name of the type.</param>
    public QueryeablesBuilder AddChangeFeed<TEntity>(string? table = null) where TEntity : class
    {
        Services.TryAddSingleton<IChangeFeed<TEntity>>(sp =>
        {
            var monitor = new ChangeFeed<TEntity>(table);
            sp.GetService<ChangeFeedRegistry>()?.Add(monitor);
            return monitor;
        });
        return this;
    }

    /// <summary>
    /// Registers the in-memory cache of <typeparamref name="TEntity"/> (<see cref="IEntityCache{TEntity, TKey}"/>), fed by the
    /// stores and kept up to date by the change feed of the type. <c>[EntityCache]</c> on the class does it for you.
    /// </summary>
    /// <remarks>
    /// With an integer key, each notification updates its row only: the change sources (the SQLite rowid of an
    /// <c>INTEGER PRIMARY KEY</c> table...) report that key. With any other key, a notification synchronizes the cache
    /// (only the changed rows with [LastModified], everything otherwise): a rowid cannot be read as a Guid or a string key.
    /// </remarks>
    public QueryeablesBuilder AddCache<[DynamicallyAccessedMembers(EntityMembers.Required | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TEntity,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TKey>(Expression<Func<TEntity, TKey>> key)
        where TEntity : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(key);
        AddChangeFeed<TEntity>();
        // Forgotten when the user changes (IServiceProvider.ChangeUserAsync).
        Services.AddSingleton<IUserScopedData>(sp => sp.GetRequiredService<IEntityCache<TEntity, TKey>>() as IUserScopedData ?? NoUserData.Instance);
        Services.TryAddSingleton<IEntityCache<TEntity, TKey>>(sp => new EntityCache<TEntity, TKey>(
            sp.GetRequiredService<IEntityStoreFactory>(),
            key,
            sp.GetRequiredService<IChangeFeed<TEntity>>(),
            IsInteger(typeof(TKey)) ? EntityKey.Parse<TKey> : null,
            sp.GetService<LocalWrites>()));
        return this;
    }

    /// <summary>
    /// Registers an entity graph (a parent and its children) and its repository, <see cref="IAggregateRepository{TParent}"/>
    /// (<see cref="GraphRepository{TParent, TKey}"/>). <c>[EntityGraph]</c> on a collection does it for you.
    /// </summary>
    public QueryeablesBuilder AddGraph<[DynamicallyAccessedMembers(EntityMembers.Required)] TParent, TKey>(
        Func<IServiceProvider, EntityGraph<TParent, TKey>> graph)
        where TParent : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(graph);
        Services.TryAddSingleton(graph);
        Services.TryAddSingleton<IAggregateRepository<TParent>>(sp => new GraphRepository<TParent, TKey>(
            sp.GetRequiredService<IEntityStoreFactory>(),
            sp.GetRequiredService<EntityGraph<TParent, TKey>>())
        {
            ThrowOnFailure = sp.GetService<QueryeablesOptions>()?.ThrowOnFailure == true,
        });
        return this;
    }

    /// <summary>
    /// The entity types of the application, with their keys: those <c>AddGeneratedEntities()</c> knows (caches, graphs and
    /// their children, references), and those added by <see cref="AddEntity{TEntity, TKey}"/>. What applies to every
    /// entity reads them: <c>ExposeGenerated</c> on a server, <c>AllGeneratedEntities</c> offline, <c>CreateTables</c>.
    /// </summary>
    public IReadOnlyList<IEntityType> Entities => _entities;

    /// <summary>Declares an entity type and its key (also registered as an <see cref="IEntityType"/> service). The first declaration wins.</summary>
    public QueryeablesBuilder AddEntity<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TKey>(Expression<Func<TEntity, TKey>> key)
        where TEntity : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_entities.Any(e => e.Type == typeof(TEntity)))
            return this;
        var type = new EntityType<TEntity, TKey>(key);
        _entities.Add(type);
        Services.AddSingleton<IEntityType>(type);
        return this;
    }

    /// <summary>
    /// Loads the cache of <typeparamref name="TEntity"/> when the application starts (<see cref="IQueryeablesStartupTask"/>):
    /// what <c>[EntityCache(Preload = true)]</c> does.
    /// </summary>
    public QueryeablesBuilder Preload<TEntity, TKey>()
        where TEntity : class
        where TKey : notnull
    {
        Services.AddSingleton<IQueryeablesStartupTask>(new QueryeablesStartupTask(100,
            (sp, ct) => sp.GetRequiredService<IEntityCache<TEntity, TKey>>().LoadAsync(ct)));
        return this;
    }

    /// <summary>
    /// The repositories throw an <see cref="EntityStoreException"/> instead of returning a failed <see cref="EntityResult"/>:
    /// for an application that prefers exceptions. The stores throw already.
    /// </summary>
    public QueryeablesBuilder ThrowOnFailure()
    {
        Options.ThrowOnFailure = true;
        return this;
    }

    /// <summary>
    /// On a remote client, waits at startup (<see cref="IQueryeablesStartupTask"/>) until the server answers: a client
    /// started with its server (a development session, a container) does not fail on its first call.
    /// </summary>
    /// <param name="timeout">How long to wait; 30 seconds by default. Beyond, the startup fails with a <see cref="TimeoutException"/>.</param>
    public QueryeablesBuilder WaitForServer(TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        Services.AddSingleton<IQueryeablesStartupTask>(new QueryeablesStartupTask(-100, async (sp, ct) =>
        {
            var transport = sp.GetRequiredService<IEntityTransport>();
            var type = sp.GetServices<IEntityType>().Select(t => EntityName.Of(t.Type)).FirstOrDefault() ?? "Ping";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(limit);
            while (true)
            {
                try
                {
                    await transport.SelectPagedAsync(type, [], false, 1, 1, deadline.Token);
                    return;                     // any answer, even an error, comes from the server
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    if (deadline.IsCancellationRequested)
                        throw new TimeoutException($"The server did not answer within {limit.TotalSeconds:0} seconds.");
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"The server did not answer within {limit.TotalSeconds:0} seconds.");
                    }
                }
            }
        }));
        return this;
    }

    /// <summary>The options the generated code reads (registered as a singleton).</summary>
    internal QueryeablesOptions Options { get; } = new();

    private static bool IsInteger(Type type)
        => Type.GetTypeCode(Nullable.GetUnderlyingType(type) ?? type) is TypeCode.Int64 or TypeCode.Int32 or TypeCode.Int16
            or TypeCode.UInt64 or TypeCode.UInt32 or TypeCode.UInt16 or TypeCode.Byte or TypeCode.SByte
           && !type.IsEnum;

    /// <summary>Registers the store factory, once everything is configured.</summary>
    internal void Build()
    {
        if (_stores is null)
            throw new InvalidOperationException(
                "Queryeables has no store: call a provider in UseQueryeables, for example q.UseSqlite(...), q.UseDommel<TConnection>(...), " +
                "q.UseEntityFramework(...) or q.UseMagicOnion(...), or q.UseStores(...) for your own store.");

        var stores = _stores;
        var syncConventions = _syncConventions;
        var offline = _offline is not null;
        Services.TryAddSingleton<LocalWrites>();
        Services.Replace(ServiceDescriptor.Singleton<IEntityStoreFactory>(sp =>
        {
            var provider = stores(sp);
            var factory = syncConventions
                ? new EntityStoreFactory(() => provider.Create().WithSyncConventions(), provider.IsRemote)
                : provider;
            if (offline)
                factory = new OfflineStoreFactory(factory, sp.GetRequiredService<OfflineMode>());
            // The writes of this application reach its caches at once.
            var writes = sp.GetRequiredService<LocalWrites>();
            var reported = factory;
            return new EntityStoreFactory(() => new LocalWritesDecorator(reported.Create(), writes), reported.IsRemote);
        }));
    }

    private sealed class NoUserData : IUserScopedData
    {
        public static readonly NoUserData Instance = new();

        public Task ClearUserDataAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>What the generated code reads of the configuration.</summary>
public sealed class QueryeablesOptions
{
    /// <inheritdoc cref="QueryeablesBuilder.ThrowOnFailure"/>
    public bool ThrowOnFailure { get; set; }
}

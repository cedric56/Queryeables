using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Queryeables.Abstraction;
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

    internal QueryeablesBuilder(IServiceCollection services) => Services = services;

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
    /// Applies [LastModified] and [SoftDelete] to every store (see <see cref="SyncConventionsStore"/>). On a remote client,
    /// apply it on the server instead.
    /// </summary>
    public QueryeablesBuilder WithSyncConventions()
    {
        _syncConventions = true;
        return this;
    }

    /// <summary>
    /// Registers the change monitor of <typeparamref name="TEntity"/> (<see cref="IDbMonitor{TEntity}"/>), added to
    /// <see cref="DbMonitorRegistry"/> so that the changes of the database reach it.
    /// </summary>
    /// <param name="table">Name of the table; by default the name of the type.</param>
    public QueryeablesBuilder AddMonitor<TEntity>(string? table = null) where TEntity : class
    {
        Services.TryAddSingleton<IDbMonitor<TEntity>>(sp =>
        {
            var monitor = new DbMonitor<TEntity>(table);
            sp.GetService<DbMonitorRegistry>()?.Add(monitor);
            return monitor;
        });
        return this;
    }

    /// <summary>
    /// Registers the in-memory cache of <typeparamref name="TEntity"/> (<see cref="IEntityCache{TEntity, TKey}"/>), fed by the
    /// stores and kept up to date by the monitor of the type. <c>[EntityCache]</c> on the class does it for you.
    /// </summary>
    /// <remarks>
    /// With an integer key, each notification updates its row only: the change sources (the SQLite rowid of an
    /// <c>INTEGER PRIMARY KEY</c> table...) report that key. With any other key, a notification synchronizes the cache
    /// (only the changed rows with [LastModified], everything otherwise): a rowid cannot be read as a Guid or a string key.
    /// </remarks>
    public QueryeablesBuilder AddCache<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TKey>(Expression<Func<TEntity, TKey>> key)
        where TEntity : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(key);
        AddMonitor<TEntity>();
        Services.TryAddSingleton<IEntityCache<TEntity, TKey>>(sp => new EntityCache<TEntity, TKey>(
            sp.GetRequiredService<IEntityStoreFactory>(),
            key,
            sp.GetRequiredService<IDbMonitor<TEntity>>(),
            IsInteger(typeof(TKey)) ? DbRecordKey.Parse<TKey> : null));
        return this;
    }

    /// <summary>
    /// Registers an entity graph (a parent and its children) and its service, <see cref="IDbTransaction{TParent}"/>
    /// (<see cref="EntityGraphService{TParent, TKey}"/>). <c>[EntityGraph]</c> on a collection does it for you.
    /// </summary>
    public QueryeablesBuilder AddGraph<[DynamicallyAccessedMembers(EntityMembers.Required)] TParent, TKey>(
        Func<IServiceProvider, EntityGraph<TParent, TKey>> graph)
        where TParent : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(graph);
        Services.TryAddSingleton(graph);
        Services.TryAddSingleton<IDbTransaction<TParent>>(sp => new EntityGraphService<TParent, TKey>(
            sp.GetRequiredService<IEntityStoreFactory>(),
            sp.GetRequiredService<EntityGraph<TParent, TKey>>()));
        return this;
    }

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
        Services.Replace(ServiceDescriptor.Singleton<IEntityStoreFactory>(sp =>
        {
            var factory = stores(sp);
            return syncConventions
                ? new EntityStoreFactory(() => factory.Create().WithSyncConventions(), factory.IsRemote)
                : factory;
        }));
    }
}

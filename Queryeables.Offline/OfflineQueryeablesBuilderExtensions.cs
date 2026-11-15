using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Queryeables.Abstraction;
using Queryeables.Offline;
using Queryeables.Remote;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

// The namespace of the builder: found in Program.cs without a using.
namespace Queryeables;

/// <summary>
/// The offline mode and the generic remote writes, plugged into the core through its extension points
/// (<see cref="QueryeablesBuilder.DecorateStores"/>, <see cref="EntityServerBuilder.Handlers"/>, <see cref="IUserScopedData"/>).
/// </summary>
public static class OfflineQueryeablesBuilderExtensions
{
    /// <summary>
    /// Client: the stores write and query through the server (<see cref="WritableRemoteEntityStore"/>), instead of reading
    /// the replicas only. The server must allow it (<see cref="AllowWrites"/>). Call it after the transport
    /// (<c>UseMagicOnion</c>...), which registers <see cref="IEntityTransport"/> and <see cref="IEntitySerializer"/>.
    /// </summary>
    public static QueryeablesBuilder UseRemoteWrites(this QueryeablesBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Services.Any(d => d.ServiceType == typeof(RemoteWritesMarker)))
            return builder;
        builder.Services.AddSingleton<RemoteWritesMarker>();
        return builder.DecorateStores((sp, factory) => factory.IsRemote
            ? WritableRemoteEntityStore.Factory(
                sp.GetRequiredService<IEntityTransport>(),
                sp.GetRequiredService<IEntitySerializer>(),
                sp.GetService<IChangeSession>())
            : factory);
    }

    /// <summary>
    /// Lets a remote client work without the server (<see cref="OfflineMode"/>, registered as a singleton): the user is told
    /// when the server is lost or back and when the data differs, and chooses when to switch. It writes through the server
    /// while online (<see cref="UseRemoteWrites"/>, implied). Call <see cref="OfflineMode.InitializeAsync"/> once at startup,
    /// before the first store is created.
    /// </summary>
    /// <example>
    /// <code>
    /// q.UseMagicOnion(url).AddGeneratedEntities()
    ///  .WithOfflineMode(o => o
    ///      .UseStorage(new IndexedDbOfflineStorage())     // WebAssembly; SqliteOfflineStorage elsewhere
    ///      .AllGeneratedEntities());
    /// </code>
    /// </example>
    public static QueryeablesBuilder WithOfflineMode(this QueryeablesBuilder builder, Action<OfflineModeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var existing = builder.Services.FirstOrDefault(d => d.ServiceType == typeof(OfflineModeOptions))?.ImplementationInstance
            as OfflineModeOptions;
        var options = existing ?? new OfflineModeOptions();
        configure(options);
        if (existing is not null)
            return builder;

        builder.UseRemoteWrites();
        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(sp => options.Create(
            sp.GetRequiredService<IEntityTransport>(),
            sp.GetRequiredService<IEntitySerializer>(),
            sp.GetService<ChangeFeedRegistry>(),
            sp));
        builder.Services.AddSingleton<IUserScopedData>(sp => sp.GetRequiredService<OfflineMode>());
        return builder.DecorateStores((sp, factory) => new OfflineStoreFactory(factory, sp.GetRequiredService<OfflineMode>()));
    }

    /// <summary>
    /// Server: the clients write through the exposed types (inserts, updates, deletes, patches, writes of many rows, graphs,
    /// batches) and query them with filters, pages and sorts (<see cref="WritableEntityHandler{T}"/>), under the access
    /// policy, the row filter and validation of each type. What <see cref="UseRemoteWrites"/> and the offline mode need.
    /// </summary>
    /// <param name="durableIdempotency">
    /// Keep the results of the writes in the database (<see cref="StoreIdempotencyStore"/>, table <c>IdempotencyRecords</c>)
    /// rather than in memory: a write sent again after a restart of the server is not applied twice. What production needs,
    /// with offline clients. An <see cref="IIdempotencyStore"/> registered before takes precedence.
    /// </param>
    public static EntityServerBuilder AllowWrites(this EntityServerBuilder server, bool durableIdempotency = false)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.Handlers = WritableHandlers.Instance;
        if (durableIdempotency)
            server.Services.TryAddSingleton<IIdempotencyStore>(sp => new StoreIdempotencyStore(sp.GetRequiredService<IEntityStoreFactory>()));
        return server;
    }

    private sealed class RemoteWritesMarker;

    private sealed class WritableHandlers : IEntityHandlerFactory
    {
        public static readonly WritableHandlers Instance = new();

        public EntityHandler Create<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(IServiceProvider services,
            ExposeOptions<TEntity> options) where TEntity : class
        {
            var aggregate = services.GetService<IAggregateRepository<TEntity>>();

            // Without its graph repository, the server would save the parent and silently drop the children it received.
            if (aggregate is null && HasEntityGraph(typeof(TEntity)))
                throw new InvalidOperationException(
                    $"{typeof(TEntity).Name} has [EntityGraph] collections, but no IAggregateRepository<{typeof(TEntity).Name}> is registered " +
                    "on the server: its children would not be saved. Call AddGeneratedEntities() in UseQueryeables (from a project " +
                    "that references the one declaring the entities), or AddGraph<TParent, TKey>(...).");

            return new WritableEntityHandler<TEntity>(services.GetRequiredService<IEntityStoreFactory>(), options.Policy, options.Validate,
                options.MaxPageSize, aggregate, options.Rows, options.MaxReadSize);
        }

        private static bool HasEntityGraph([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type)
            => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => property.IsDefined(typeof(EntityGraphAttribute), inherit: true));
    }
}

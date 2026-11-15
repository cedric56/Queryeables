using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using Queryeables.Remote;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Queryeables;

/// <summary>
/// The entity types a server exposes to its remote clients (see <see cref="EntityServerExtensions.AddEntityServer"/>).
/// A type that is not exposed cannot be reached; each exposed type has its own access policy.
/// </summary>
public sealed class EntityServerBuilder
{
    private readonly List<Func<IServiceProvider, EntityHandler>> _handlers = [];
    private readonly HashSet<Type> _exposed = [];
    private (IEntityAccessPolicy Policy, bool Validate, int MaxPageSize, int MaxReadSize)? _generated;

    internal EntityServerBuilder()
    {
    }

    /// <summary>
    /// Send the message of unexpected errors (database errors...) to the clients. Off by default: they get a reference,
    /// and the error is logged under it. Development only: database messages name tables, constraints and values.
    /// </summary>
    public bool DetailedErrors { get; set; }

    /// <summary>Most operations a client may send in one batch (one transaction).</summary>
    public int MaxBatchSize { get; set; } = 100;

    /// <summary>
    /// Keep the results of the writes in the database (<see cref="StoreIdempotencyStore"/>, table <c>IdempotencyRecords</c>)
    /// rather than in memory: a write sent again after a restart of the server is not applied twice. What production
    /// needs, with offline clients. An <see cref="IIdempotencyStore"/> registered before takes precedence.
    /// </summary>
    public bool DurableIdempotency { get; set; }

    /// <summary>
    /// Exposes <typeparamref name="TEntity"/>. When an <see cref="IAggregateRepository{TEntity}"/> is registered for it (an entity
    /// graph), writes go through it: the entity is saved with its children.
    /// </summary>
    /// <param name="policy">Who may read and write. Never <see cref="EntityAccessPolicies.AllowAll"/> on a public network.</param>
    /// <param name="validate">Validates entities (DataAnnotations) before insert and update.</param>
    /// <param name="maxPageSize">Maximum page size: a larger page is reduced to this size.</param>
    /// <param name="rows">
    /// The rows each user may reach, added to every read and checked on every write
    /// (<see cref="RowLevelSecurity.For{T, TKey}"/>). Without it, a user allowed by the policy reaches every row.
    /// </param>
    /// <param name="maxReadSize">Most rows a read that is not paged may return; beyond it, the read fails.</param>
    public EntityServerBuilder Expose<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        IEntityAccessPolicy policy, bool validate = true, int maxPageSize = 1000, RowLevelSecurity<TEntity>? rows = null,
        int maxReadSize = EntityHandler.DefaultMaxReadSize)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(policy);
        _exposed.Add(typeof(TEntity));
        _handlers.Add(sp => Handler(sp, policy, validate, maxPageSize, rows, maxReadSize));
        return this;
    }

    /// <summary>
    /// Exposes every entity type the generator knows (<c>AddGeneratedEntities()</c>: caches, graphs and their children,
    /// references) with <paramref name="policy"/>, except those exposed one by one (<see cref="Expose{TEntity}"/>, before or
    /// after), which keep their own policy and row filter.
    /// </summary>
    /// <inheritdoc cref="Expose{TEntity}" path="/param[@name='policy' or @name='validate' or @name='maxPageSize' or @name='maxReadSize']"/>
    public EntityServerBuilder ExposeGenerated(IEntityAccessPolicy policy, bool validate = true, int maxPageSize = 1000,
        int maxReadSize = EntityHandler.DefaultMaxReadSize)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _generated = (policy, validate, maxPageSize, maxReadSize);
        return this;
    }

    private static EntityHandler<TEntity> Handler<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(IServiceProvider sp,
        IEntityAccessPolicy policy, bool validate, int maxPageSize, RowLevelSecurity<TEntity>? rows, int maxReadSize)
        where TEntity : class
    {
        var aggregate = sp.GetService<IAggregateRepository<TEntity>>();

        // Without its graph repository, the server would save the parent and silently drop the children it received.
        if (aggregate is null && HasEntityGraph(typeof(TEntity)))
            throw new InvalidOperationException(
                $"{typeof(TEntity).Name} has [EntityGraph] collections, but no IAggregateRepository<{typeof(TEntity).Name}> is registered " +
                "on the server: its children would not be saved. Call AddGeneratedEntities() in UseQueryeables (from a project " +
                "that references the one declaring the entities), or AddGraph<TParent, TKey>(...).");

        return new EntityHandler<TEntity>(sp.GetRequiredService<IEntityStoreFactory>(), policy, validate, maxPageSize, aggregate, rows,
            maxReadSize);
    }

    /// <summary>Creates the handler of each generated entity type not exposed one by one.</summary>
    private sealed class GeneratedHandlers(IServiceProvider services, (IEntityAccessPolicy Policy, bool Validate, int MaxPageSize, int MaxReadSize) options)
        : IEntityTypeVisitor
    {
        public List<EntityHandler> Handlers { get; } = [];

        public void Visit<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity,
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TKey>(EntityType<TEntity, TKey> type)
            where TEntity : class
            where TKey : notnull
            => Handlers.Add(Handler<TEntity>(services, options.Policy, options.Validate, options.MaxPageSize, null, options.MaxReadSize));
    }

    private static bool HasEntityGraph([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property => property.IsDefined(typeof(EntityGraphAttribute), inherit: true));

    internal EntityHandlerRegistry Build(IServiceProvider services)
    {
        var registry = new EntityHandlerRegistry();
        foreach (var handler in _handlers)
            registry.Register(handler(services));
        if (_generated is { } generated)
        {
            var types = services.GetServices<IEntityType>().Where(t => !_exposed.Contains(t.Type)).ToList();
            if (types.Count == 0 && _exposed.Count == 0)
                throw new InvalidOperationException(
                    "ExposeGenerated found no entity: call AddGeneratedEntities() in UseQueryeables, from a project that references " +
                    "the one declaring the entities.");
            var visitor = new GeneratedHandlers(services, generated);
            foreach (var type in types)
                type.Accept(visitor);
            foreach (var handler in visitor.Handlers)
                registry.Register(handler);
        }
        return registry;
    }
}

public static class EntityServerExtensions
{
    /// <summary>
    /// Makes this application a Queryeables server: registers <see cref="EntityRequestDispatcher"/>, which a transport
    /// (the MagicOnion service, an HTTP endpoint...) calls for its clients. Remote transactions run on the stores of this
    /// application.
    /// </summary>
    /// <param name="serializer">The entity serializer of the transport, the same as the clients'.</param>
    /// <param name="expose">The exposed entity types and their access policies.</param>
    public static QueryeablesBuilder AddEntityServer(this QueryeablesBuilder builder, IEntitySerializer serializer,
        Action<EntityServerBuilder> expose)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(expose);

        var server = new EntityServerBuilder();
        expose(server);

        builder.Services.TryAddSingleton(serializer);
        builder.Services.TryAddSingleton<IEntityHandlerRegistry>(server.Build);
        builder.Services.TryAddSingleton<IIdempotencyStore>(sp => server.DurableIdempotency
            ? new StoreIdempotencyStore(sp.GetRequiredService<IEntityStoreFactory>())
            : new MemoryIdempotencyStore());
        builder.Services.TryAddSingleton<ConnectionOwners>();
        builder.Services.TryAddSingleton(sp => Warned(sp, new EntityRequestDispatcher(
            sp.GetRequiredService<IEntityHandlerRegistry>(),
            sp.GetRequiredService<IEntitySerializer>(),
            sp.GetRequiredService<IEntityStoreFactory>())
        {
            Idempotency = sp.GetRequiredService<IIdempotencyStore>(),
            Connections = sp.GetRequiredService<ConnectionOwners>(),
            DetailedErrors = server.DetailedErrors,
            MaxBatchSize = server.MaxBatchSize,
            UnexpectedError = sp.GetService<ILoggerFactory>()?.CreateLogger<EntityRequestDispatcher>() is { } logger
                ? (exception, reference) => logger.LogError(exception, "Unexpected error, reference {Reference}.", reference)
                : null,
        }));
        return builder;
    }

    /// <summary>Says once, at the creation of the dispatcher, that the results of the writes do not survive a restart.</summary>
    private static EntityRequestDispatcher Warned(IServiceProvider services, EntityRequestDispatcher dispatcher)
    {
        if (dispatcher.Idempotency is MemoryIdempotencyStore)
            services.GetService<ILoggerFactory>()?.CreateLogger<EntityRequestDispatcher>().LogWarning(
                "The results of the writes are kept in memory: a write an offline client sends again after a restart of " +
                "this server is applied twice. In production, set DurableIdempotency (or register an IIdempotencyStore).");
        return dispatcher;
    }
}

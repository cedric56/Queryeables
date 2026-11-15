using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using Queryeables.Remote;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables;

/// <summary>
/// The entity types a server publishes to its remote clients (see <see cref="EntityServerExtensions.AddEntityServer"/>):
/// each client keeps a replica of the rows it may read. A type that is not exposed cannot be reached; each exposed type has
/// its own access policy.
/// </summary>
public sealed class EntityServerBuilder
{
    private readonly List<Func<IServiceProvider, EntityHandler>> _handlers = [];
    private readonly HashSet<Type> _exposed = [];
    private (IEntityAccessPolicy Policy, bool Validate, int MaxPageSize, int MaxReadSize)? _generated;

    internal EntityServerBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>The services of the application: what an extension registers with its handlers.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Creates the handler of each exposed type: <see cref="EntityHandler{T}"/> (replicas, read only) unless an extension
    /// replaces it (<c>AllowWrites()</c> of <c>Queryeables.Offline</c>).
    /// </summary>
    public IEntityHandlerFactory Handlers { get; set; } = ReplicaHandlers.Instance;

    /// <summary>
    /// Send the message of unexpected errors (database errors...) to the clients. Off by default: they get a reference,
    /// and the error is logged under it. Development only: database messages name tables, constraints and values.
    /// </summary>
    public bool DetailedErrors { get; set; }

    /// <summary>Most operations a client may send in one batch (one transaction), when writes are allowed.</summary>
    public int MaxBatchSize { get; set; } = 100;

    /// <summary>Exposes <typeparamref name="TEntity"/>.</summary>
    /// <param name="policy">Who may read (and write, when writes are allowed). Never <see cref="EntityAccessPolicies.AllowAll"/> on a public network.</param>
    /// <param name="validate">Validates entities (DataAnnotations) before insert and update, when writes are allowed.</param>
    /// <param name="maxPageSize">Maximum page size, when pages are served: a larger page is reduced to this size.</param>
    /// <param name="rows">
    /// The rows each user may reach, added to every read (and checked on every write)
    /// (<see cref="RowLevelSecurity.For{T, TKey}"/>). Without it, a user allowed by the policy reaches every row.
    /// </param>
    /// <param name="maxReadSize">Most rows a replica (a read that is not paged) may hold; beyond it, the read fails.</param>
    public EntityServerBuilder Expose<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        IEntityAccessPolicy policy, bool validate = true, int maxPageSize = 1000, RowLevelSecurity<TEntity>? rows = null,
        int maxReadSize = EntityHandler.DefaultMaxReadSize)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(policy);
        _exposed.Add(typeof(TEntity));
        _handlers.Add(sp => Handlers.Create(sp, new ExposeOptions<TEntity>(policy, validate, maxPageSize, rows, maxReadSize)));
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

    /// <summary>Creates the handler of each generated entity type not exposed one by one.</summary>
    private sealed class GeneratedHandlers(IServiceProvider services, IEntityHandlerFactory factory,
        (IEntityAccessPolicy Policy, bool Validate, int MaxPageSize, int MaxReadSize) options) : IEntityTypeVisitor
    {
        public List<EntityHandler> Handlers { get; } = [];

        public void Visit<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity,
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TKey>(EntityType<TEntity, TKey> type)
            where TEntity : class
            where TKey : notnull
            => Handlers.Add(factory.Create(services,
                new ExposeOptions<TEntity>(options.Policy, options.Validate, options.MaxPageSize, null, options.MaxReadSize)));
    }

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
            var visitor = new GeneratedHandlers(services, Handlers, generated);
            foreach (var type in types)
                type.Accept(visitor);
            foreach (var handler in visitor.Handlers)
                registry.Register(handler);
        }
        return registry;
    }

    /// <summary>The handlers of the core: replicas, read only.</summary>
    private sealed class ReplicaHandlers : IEntityHandlerFactory
    {
        public static readonly ReplicaHandlers Instance = new();

        public EntityHandler Create<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(IServiceProvider services,
            ExposeOptions<TEntity> options) where TEntity : class
            => new EntityHandler<TEntity>(services.GetRequiredService<IEntityStoreFactory>(), options.Policy, options.Rows, options.MaxReadSize);
    }
}

/// <summary>How a type is exposed (see <see cref="EntityServerBuilder.Expose{TEntity}"/>).</summary>
public sealed record ExposeOptions<TEntity>(IEntityAccessPolicy Policy, bool Validate, int MaxPageSize, RowLevelSecurity<TEntity>? Rows,
    int MaxReadSize) where TEntity : class;

/// <summary>Creates the handler of an exposed type: the extension point of what a server serves (<see cref="EntityServerBuilder.Handlers"/>).</summary>
public interface IEntityHandlerFactory
{
    EntityHandler Create<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(IServiceProvider services, ExposeOptions<TEntity> options)
        where TEntity : class;
}

public static class EntityServerExtensions
{
    /// <summary>
    /// Makes this application a Queryeables server: registers <see cref="EntityRequestDispatcher"/>, which a transport
    /// (the MagicOnion service, an HTTP endpoint...) calls for its clients: each client keeps a replica of the exposed types,
    /// the rows it may read.
    /// </summary>
    /// <param name="serializer">The entity serializer of the transport, the same as the clients'.</param>
    /// <param name="expose">The exposed entity types and their access policies.</param>
    public static QueryeablesBuilder AddEntityServer(this QueryeablesBuilder builder, IEntitySerializer serializer,
        Action<EntityServerBuilder> expose)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(expose);

        var server = new EntityServerBuilder(builder.Services);
        expose(server);

        builder.Services.TryAddSingleton(serializer);
        builder.Services.TryAddSingleton<IEntityHandlerRegistry>(server.Build);
        builder.Services.TryAddSingleton<IIdempotencyStore, MemoryIdempotencyStore>();
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
        if (dispatcher.Idempotency is MemoryIdempotencyStore
            && services.GetRequiredService<IEntityHandlerRegistry>().Handlers.Any(h => h.AcceptsWrites))
            services.GetService<ILoggerFactory>()?.CreateLogger<EntityRequestDispatcher>().LogWarning(
                "The results of the writes are kept in memory: a write an offline client sends again after a restart of " +
                "this server is applied twice. In production, use AllowWrites(durableIdempotency: true) (or register an IIdempotencyStore).");
        return dispatcher;
    }
}

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
    private (IEntityAccessPolicy Policy, int MaxReadSize)? _generated;

    internal EntityServerBuilder()
    {
    }

    /// <summary>
    /// Send the message of unexpected errors (database errors...) to the clients. Off by default: they get a reference,
    /// and the error is logged under it. Development only: database messages name tables, constraints and values.
    /// </summary>
    public bool DetailedErrors { get; set; }

    /// <summary>Exposes <typeparamref name="TEntity"/>.</summary>
    /// <param name="policy">Who may read it. Never <see cref="EntityAccessPolicies.AllowAll"/> on a public network.</param>
    /// <param name="rows">
    /// The rows each user may read, added to every read (<see cref="RowLevelSecurity.For{T, TKey}"/>). Without it, a user
    /// allowed by the policy reads every row.
    /// </param>
    /// <param name="maxReadSize">Most rows a replica may hold; beyond it, the read fails instead of being cut.</param>
    public EntityServerBuilder Expose<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        IEntityAccessPolicy policy, RowLevelSecurity<TEntity>? rows = null, int maxReadSize = EntityHandler.DefaultMaxReadSize)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(policy);
        _exposed.Add(typeof(TEntity));
        _handlers.Add(sp => new EntityHandler<TEntity>(sp.GetRequiredService<IEntityStoreFactory>(), policy, rows, maxReadSize));
        return this;
    }

    /// <summary>
    /// Exposes every entity type the generator knows (<c>AddGeneratedEntities()</c>: caches, graphs and their children,
    /// references) with <paramref name="policy"/>, except those exposed one by one (<see cref="Expose{TEntity}"/>, before or
    /// after), which keep their own policy and row filter.
    /// </summary>
    /// <inheritdoc cref="Expose{TEntity}" path="/param[@name='policy' or @name='maxReadSize']"/>
    public EntityServerBuilder ExposeGenerated(IEntityAccessPolicy policy, int maxReadSize = EntityHandler.DefaultMaxReadSize)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _generated = (policy, maxReadSize);
        return this;
    }

    /// <summary>Creates the handler of each generated entity type not exposed one by one.</summary>
    private sealed class GeneratedHandlers(IEntityStoreFactory stores, IEntityAccessPolicy policy, int maxReadSize) : IEntityTypeVisitor
    {
        public List<EntityHandler> Handlers { get; } = [];

        public void Visit<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity,
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TKey>(EntityType<TEntity, TKey> type)
            where TEntity : class
            where TKey : notnull
            => Handlers.Add(new EntityHandler<TEntity>(stores, policy, null, maxReadSize));
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
            var visitor = new GeneratedHandlers(services.GetRequiredService<IEntityStoreFactory>(), generated.Policy, generated.MaxReadSize);
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

        var server = new EntityServerBuilder();
        expose(server);

        builder.Services.TryAddSingleton(serializer);
        builder.Services.TryAddSingleton<IEntityHandlerRegistry>(server.Build);
        builder.Services.TryAddSingleton<ConnectionOwners>();
        builder.Services.TryAddSingleton(sp => new EntityRequestDispatcher(
            sp.GetRequiredService<IEntityHandlerRegistry>(),
            sp.GetRequiredService<IEntitySerializer>())
        {
            DetailedErrors = server.DetailedErrors,
            UnexpectedError = sp.GetService<ILoggerFactory>()?.CreateLogger<EntityRequestDispatcher>() is { } logger
                ? (exception, reference) => logger.LogError(exception, "Unexpected error, reference {Reference}.", reference)
                : null,
        });
        return builder;
    }
}

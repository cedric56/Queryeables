using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    internal EntityServerBuilder()
    {
    }

    /// <summary>
    /// Exposes <typeparamref name="TEntity"/>. When an <see cref="IDbTransaction{TEntity}"/> is registered for it (an entity
    /// graph), writes go through it: the entity is saved with its children.
    /// </summary>
    /// <param name="policy">Who may read and write. Never <see cref="EntityAccessPolicies.AllowAll"/> on a public network.</param>
    /// <param name="validate">Validates entities (DataAnnotations) before insert and update.</param>
    /// <param name="maxPageSize">Maximum page size: a larger page is reduced to this size.</param>
    public EntityServerBuilder Expose<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        IEntityAccessPolicy policy, bool validate = true, int maxPageSize = 1000)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(policy);
        _handlers.Add(sp =>
        {
            var aggregate = sp.GetService<IDbTransaction<TEntity>>();

            // Without its graph service, the server would save the parent and silently drop the children it received.
            if (aggregate is null && HasEntityGraph(typeof(TEntity)))
                throw new InvalidOperationException(
                    $"{typeof(TEntity).Name} has [EntityGraph] collections, but no IDbTransaction<{typeof(TEntity).Name}> is registered " +
                    "on the server: its children would not be saved. Call AddGeneratedEntities() in UseQueryeables (from a project " +
                    "that references the one declaring the entities), or AddGraph<TParent, TKey>(...).");

            return new EntityHandler<TEntity>(sp.GetRequiredService<IEntityStoreFactory>(), policy, validate, maxPageSize, aggregate);
        });
        return this;
    }

    private static bool HasEntityGraph([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property => property.IsDefined(typeof(EntityGraphAttribute), inherit: true));

    internal EntityHandlerRegistry Build(IServiceProvider services)
    {
        var registry = new EntityHandlerRegistry();
        foreach (var handler in _handlers)
            registry.Register(handler(services));
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
        builder.Services.TryAddSingleton(sp => new EntityRequestDispatcher(
            sp.GetRequiredService<IEntityHandlerRegistry>(),
            sp.GetRequiredService<IEntitySerializer>(),
            sp.GetRequiredService<IEntityStoreFactory>()));
        return builder;
    }
}

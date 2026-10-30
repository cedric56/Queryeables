using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Queryeables.Abstraction;

namespace Queryeables;

public static class QueryeablesServiceCollectionExtensions
{
    /// <summary>
    /// Starts Queryeables: one store provider, then the entities.
    /// Registers <see cref="IEntityStoreFactory"/>, <see cref="DbMonitorRegistry"/> and <see cref="IDbSession"/>,
    /// and whatever <paramref name="configure"/> adds.
    /// </summary>
    /// <example>
    /// <code>
    /// services.UseQueryeables(q => q.UseSqlite("Data Source=app.db").AddGeneratedEntities());          // local
    /// services.UseQueryeables(q => q.UseMagicOnion("https://server:5001").AddGeneratedEntities());    // remote
    /// </code>
    /// </example>
    public static IServiceCollection UseQueryeables(this IServiceCollection services, Action<QueryeablesBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<DbMonitorRegistry>();
        services.TryAddSingleton<IDbSession, DbSession>();

        var builder = new QueryeablesBuilder(services);
        configure(builder);
        builder.Build();
        return services;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Queryeables.Abstraction;

namespace Queryeables;

public static class QueryeablesServiceCollectionExtensions
{
    /// <summary>
    /// Starts Queryeables: one store provider, then the entities.
    /// Registers <see cref="IEntityStoreFactory"/>, <see cref="ChangeFeedRegistry"/> and <see cref="IChangeSession"/>,
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

        services.TryAddSingleton<ChangeFeedRegistry>();
        services.TryAddSingleton<IChangeSession, ChangeSession>();

        var builder = new QueryeablesBuilder(services);
        configure(builder);
        builder.Build();
        return services;
    }

    /// <summary>
    /// Changes the user of a client: forgets what the caches and the offline copy hold for the previous one, then loads
    /// the caches again for the new one (see <see cref="UserScopedData.ChangeUserAsync"/>). Call it after sign-out or
    /// sign-in, once the transport sends the new credentials.
    /// </summary>
    /// <exception cref="InvalidOperationException">Offline writes are waiting: synchronize or discard them first. Nothing was forgotten.</exception>
    public static Task ChangeUserAsync(this IServiceProvider services, bool reload = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return UserScopedData.ChangeUserAsync(services.GetServices<IUserScopedData>(), reload, ct);
    }
}

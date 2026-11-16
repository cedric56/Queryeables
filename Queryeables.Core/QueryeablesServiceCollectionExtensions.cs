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
        services.TryAddSingleton<QueryeablesStartup>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, QueryeablesStartup>(
            sp => sp.GetRequiredService<QueryeablesStartup>()));

        var builder = new QueryeablesBuilder(services);
        configure(builder);
        builder.Build();
        return services;
    }

    /// <summary>
    /// Brings every registered cache up to date with its store: after the store was written behind the change feeds, for
    /// example a Datasync pull into the local database (<c>Queryeables.Datasync</c>). Only the caches already loaded, and only
    /// the changed rows for the types with <c>[LastModified]</c>; the others are reloaded.
    /// </summary>
    public static async Task RefreshCachesAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var cache in services.GetServices<IUserScopedData>().OfType<ISynchronizableCache>())
            await cache.SyncAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes the user of a client: forgets what the caches (and an offline copy) hold for the previous one, then loads
    /// the caches again for the new one (see <see cref="UserScopedData.ChangeUserAsync"/>). Call it after sign-out or
    /// sign-in, once the transport sends the new credentials.
    /// </summary>
    /// <exception cref="InvalidOperationException">Writes are waiting to be sent: save or discard them first. Nothing was forgotten.</exception>
    public static Task ChangeUserAsync(this IServiceProvider services, bool reload = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return UserScopedData.ChangeUserAsync(services.GetServices<IUserScopedData>(), reload, ct);
    }
}

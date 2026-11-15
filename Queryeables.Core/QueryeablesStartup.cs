using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Queryeables;

/// <summary>
/// Work done once when the application starts, before it uses its data: creating the tables (<c>CreateTables()</c>),
/// loading the caches marked <c>[EntityCache(Preload = true)]</c>. Run by the host (a hosted service), by
/// <see cref="QueryeablesStartupExtensions.StartQueryeablesAsync"/>, or by <see cref="QueryeablesHost.CreateAsync"/>.
/// </summary>
public interface IQueryeablesStartupTask
{
    /// <summary>Lower first: the tables (0) before the caches (100).</summary>
    int Order { get; }

    Task RunAsync(IServiceProvider services, CancellationToken ct);
}

/// <summary>A startup task from a function.</summary>
public sealed class QueryeablesStartupTask(int order, Func<IServiceProvider, CancellationToken, Task> run) : IQueryeablesStartupTask
{
    public int Order { get; } = order;

    public Task RunAsync(IServiceProvider services, CancellationToken ct) => run(services, ct);
}

/// <summary>Runs the startup tasks once per service provider.</summary>
internal sealed class QueryeablesStartup(IServiceProvider services, IEnumerable<IQueryeablesStartupTask> tasks) : IHostedService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _done;

    public async Task RunAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_done)
                return;
            foreach (var task in tasks.OrderBy(t => t.Order))
                await task.RunAsync(services, ct);
            _done = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken);

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class QueryeablesStartupExtensions
{
    /// <summary>
    /// Runs the startup tasks (tables, preloaded caches) once. A host runs them itself; call it in an application without a
    /// host (a console, a desktop application built on a <c>ServiceCollection</c>).
    /// </summary>
    public static Task StartQueryeablesAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<QueryeablesStartup>().RunAsync(ct);
    }
}

/// <summary>An application without a host, in one call: the services, configured and started.</summary>
public static class QueryeablesHost
{
    /// <summary>
    /// Builds the services of <paramref name="configure"/> (<see cref="QueryeablesServiceCollectionExtensions.UseQueryeables"/>)
    /// and runs the startup tasks: what a console or desktop application needs. Dispose the provider at the end.
    /// </summary>
    /// <example>
    /// <code>
    /// await using var app = await QueryeablesHost.CreateAsync(q => q.UseSqlite("Data Source=app.db").AddGeneratedEntities().CreateTables());
    /// var persons = app.GetRequiredService&lt;IPersonRepository&gt;();
    /// </code>
    /// </example>
    /// <param name="services">More services of the application, added before the provider is built.</param>
    public static async Task<ServiceProvider> CreateAsync(Action<QueryeablesBuilder> configure,
        Action<IServiceCollection>? services = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var collection = new ServiceCollection();
        collection.UseQueryeables(configure);
        services?.Invoke(collection);
        var provider = collection.BuildServiceProvider();
        try
        {
            await provider.StartQueryeablesAsync(ct);
            return provider;
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }
}

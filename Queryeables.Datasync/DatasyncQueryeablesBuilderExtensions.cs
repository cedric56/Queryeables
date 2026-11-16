using CommunityToolkit.Datasync.Client.Offline;
using System.Diagnostics.CodeAnalysis;

// The namespace of the builder: found in Program.cs without a using.
namespace Queryeables;

/// <summary>
/// Queryeables on the local database of a Datasync client. Datasync keeps the copy and synchronizes it (push, pull,
/// conflicts); Queryeables reads and writes it like any EF Core database, with its caches and graphs.
/// </summary>
public static class DatasyncQueryeablesBuilderExtensions
{
    private const string Trimming =
        "EF Core is not fully compatible with trimming: its model and queries use reflection. See https://aka.ms/efcore-docs-trimming.";

    private const string DynamicCode = "EF Core generates code at run time and is not compatible with Native AOT.";

    /// <summary>
    /// Stores on the offline database of a Datasync client: each store owns a new context, disposed with the store. The
    /// writes go through the context, which queues them for <c>PushAsync</c>.
    /// </summary>
    /// <param name="createContext">Creates a context, for example <c>sp =&gt; new AppDbContext(options)</c>.</param>
    /// <remarks>
    /// <c>UpdateWhere</c> and <c>DeleteWhere</c> run in the database without the change tracker: Datasync does not see
    /// them and does not push them. Update and delete the rows one by one in a synchronized table.
    /// </remarks>
    [RequiresUnreferencedCode(Trimming)]
    [RequiresDynamicCode(DynamicCode)]
    public static QueryeablesBuilder UseDatasync<TContext>(this QueryeablesBuilder builder, Func<IServiceProvider, TContext> createContext)
        where TContext : OfflineDbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createContext);
        return builder.UseEntityFramework(createContext);
    }
}

/// <summary>What follows a synchronization of Datasync on the side of Queryeables.</summary>
public static class DatasyncSynchronizationExtensions
{
    /// <summary>
    /// Waits for a synchronization (a pull, or a push that wrote the server's values back), then brings the caches up to
    /// date with what it wrote into the local database (<see cref="QueryeablesServiceCollectionExtensions.RefreshCachesAsync"/>).
    /// </summary>
    /// <example><code>var result = await context.PullAsync().ThenRefreshCachesAsync(services);</code></example>
    /// <returns>The result of the synchronization.</returns>
    public static async Task<TResult> ThenRefreshCachesAsync<TResult>(this Task<TResult> synchronization, IServiceProvider services,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(synchronization);
        ArgumentNullException.ThrowIfNull(services);
        var result = await synchronization.ConfigureAwait(false);
        await services.RefreshCachesAsync(ct).ConfigureAwait(false);
        return result;
    }
}

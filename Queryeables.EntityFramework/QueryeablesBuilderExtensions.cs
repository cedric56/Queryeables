using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables.EntityFramework;

public static class QueryeablesBuilderExtensions
{
    /// <summary>Stores with Entity Framework Core: each store owns a new context, disposed with the store.</summary>
    /// <param name="createContext">Creates a context, for example <c>sp => new AppDbContext(options)</c>.</param>
    [RequiresUnreferencedCode(EfAot.Trimming)]
    [RequiresDynamicCode(EfAot.DynamicCode)]
    public static QueryeablesBuilder UseEntityFramework<TContext>(this QueryeablesBuilder builder,
        Func<IServiceProvider, TContext> createContext)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createContext);
        return builder.UseStores(sp => new EfEntityStore(createContext(sp)));
    }
}

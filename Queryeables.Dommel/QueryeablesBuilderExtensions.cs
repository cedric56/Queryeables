using Dommel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using System.Data.Common;
using Queryeables.Dommel;

namespace Queryeables;

/// <summary>The Dommel provider of <see cref="QueryeablesBuilder"/>: in the namespace of the builder, found without a using.</summary>
public static class DommelQueryeablesBuilderExtensions
{
    /// <summary>
    /// Stores with Dommel on <typeparamref name="TConnection"/> (SqlConnection, MySqlConnection...). For SQLite and PostgreSQL,
    /// prefer <c>UseSqlite</c> and <c>UseNpgsql</c>.
    /// </summary>
    /// <param name="logCommands">Logs every SQL command (category "Queryeables.SQL"). For development.</param>
    public static QueryeablesBuilder UseDommel<TConnection>(this QueryeablesBuilder builder, string connectionString,
        bool logCommands = false, LogLevel commandLogLevel = LogLevel.Debug, bool logParameterValues = false)
        where TConnection : DbConnection, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.UseFactory<TConnection>(connectionString, logCommands, commandLogLevel: commandLogLevel,
            logParameterValues: logParameterValues);
        return builder.UseDommelStores();
    }

    /// <summary>Stores with Dommel on the registered <see cref="DbProviderFactory"/>.</summary>
    public static QueryeablesBuilder UseDommelStores(this QueryeablesBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseStores(sp =>
        {
            var provider = sp.GetRequiredService<DbProviderFactory>();
            return new EntityStoreFactory(() => DommelEntityStore.Create(provider));
        });
    }

    /// <summary>
    /// Dommel's plural table names (<c>Person</c> → <c>Persons</c>, <c>Category</c> → <c>Categories</c>) instead of the
    /// class name, for a database made that way. A setting of Dommel, for the whole application.
    /// </summary>
    public static QueryeablesBuilder PluralizeTableNames(this QueryeablesBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        DommelMapper.SetTableNameResolver(new QueryeablesTableNameResolver(pluralize: true));
        return builder;
    }

    /// <summary>
    /// Creates the tables of the application's entities (those of <c>AddGeneratedEntities()</c>, and <c>AddEntity</c>) that
    /// do not exist yet, when the application starts (<see cref="IQueryeablesStartupTask"/>: run by the host, or by
    /// <c>StartQueryeablesAsync</c>). See <see cref="DommelSchema"/>: for a first application, not a migration tool.
    /// </summary>
    public static QueryeablesBuilder CreateTables(this QueryeablesBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IQueryeablesStartupTask>(new QueryeablesStartupTask(0, (sp, ct) =>
            DommelSchema.CreateTablesAsync(sp.GetRequiredService<DbProviderFactory>(),
                sp.GetServices<IEntityType>().Select(t => t.Type).Distinct(), ct)));
        return builder;
    }
}

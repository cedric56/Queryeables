using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using System.Data.Common;

namespace Queryeables.Dommel;

public static class QueryeablesBuilderExtensions
{
    /// <summary>
    /// Stores with Dommel on <typeparamref name="TConnection"/> (SqlConnection, MySqlConnection...). For SQLite and PostgreSQL,
    /// prefer <c>UseSqlite</c> and <c>UseNpgsql</c>.
    /// </summary>
    /// <param name="logCommands">Logs every SQL command (category "Queryeables.SQL"). For development.</param>
    public static QueryeablesBuilder UseDommel<TConnection>(this QueryeablesBuilder builder, string connectionString,
        bool logCommands = false, LogLevel commandLogLevel = LogLevel.Information)
        where TConnection : DbConnection, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.UseFactory<TConnection>(connectionString, logCommands, commandLogLevel: commandLogLevel);
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
}

using Microsoft.Extensions.Logging;
using Queryeables.Dommel;

namespace Queryeables.PostgreSql;

public static class QueryeablesBuilderExtensions
{
    /// <summary>Stores with Dommel on PostgreSQL (Npgsql).</summary>
    /// <param name="logCommands">Logs every SQL command (category "Queryeables.SQL"). For development.</param>
    public static QueryeablesBuilder UseNpgsql(this QueryeablesBuilder builder, string connectionString,
        bool logCommands = false, LogLevel commandLogLevel = LogLevel.Information)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.UseNpgsqlFactory(connectionString, logCommands, commandLogLevel);
        return builder.UseDommelStores();
    }
}

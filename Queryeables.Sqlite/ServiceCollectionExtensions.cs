using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using Queryeables.Dommel;

namespace Queryeables.Sqlite;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="SqliteDbFactory"/>: database changes are reported to the monitors
    /// of <see cref="DbMonitorRegistry"/>.
    /// </summary>
    public static IServiceCollection UseSqliteFactory(
        this IServiceCollection services,
        string connectionString,
        bool isTransaction = true,
        bool logCommands = false,
        LogLevel commandLogLevel = LogLevel.Information,
        Action<SqliteDbFactory>? createDb = null)
        => services.UseFactory<SqliteConnection>(connectionString, logCommands,
            (sp, options, logger) =>
            {
                var factory = new SqliteDbFactory(connectionString, sp.GetRequiredService<DbMonitorRegistry>(), isTransaction, options, logger);
                createDb?.Invoke(factory);
                return factory;
            },
            commandLogLevel);
}

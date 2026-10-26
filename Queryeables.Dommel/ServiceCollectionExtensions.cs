using Dommel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using System.Data.Common;

namespace Queryeables.Dommel;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the connection factory (<see cref="DbProviderFactory"/>), as well as
    /// <see cref="DbMonitorRegistry"/> and <see cref="IDbSession"/> when they are not registered yet.
    /// </summary>
    /// <param name="logCommands">Logs every SQL command (category "Queryeables.SQL").</param>
    /// <param name="factory">Custom factory, for example a <c>SqliteDbFactory</c>. Default: <see cref="DbFactory{TDbConnection}"/>.</param>
    /// <param name="ignoreNotMapped">
    /// Makes Dommel ignore [NotMapped] properties. This Dommel setting is global: pass false if you
    /// have your own resolver, and wrap it in <see cref="NotMappedPropertyResolver"/>.
    /// </param>
    public static IServiceCollection UseFactory<TDbConnection>(this IServiceCollection services,
        string connectionString,
        bool logCommands = false,
        Func<IServiceProvider, DbFactoryOptions, ILogger?, DbProviderFactory>? factory = null,
        LogLevel commandLogLevel = LogLevel.Information,
        bool ignoreNotMapped = true)
        where TDbConnection : DbConnection, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (ignoreNotMapped)
            DommelMapper.SetPropertyResolver(new NotMappedPropertyResolver());

        var options = new DbFactoryOptions
        {
            LogCommands = logCommands,
            CommandLogLevel = commandLogLevel
        };

        services.TryAddSingleton<DbMonitorRegistry>();
        services.TryAddSingleton<IDbSession, DbSession>();
        services.AddSingleton(options);
        services.AddSingleton<DbProviderFactory>(sp =>
        {
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("Queryeables.SQL");
            return factory is not null
                ? factory(sp, options, logger)
                : new DbFactory<TDbConnection>(connectionString, options, logger);
        });

        return services;
    }
}

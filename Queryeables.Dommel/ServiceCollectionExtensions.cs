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
    /// Enregistre la fabrique de connexions (<see cref="DbProviderFactory"/>), ainsi que
    /// <see cref="DbMonitorRegistry"/> et <see cref="IDbSession"/> s'ils ne le sont pas déjà.
    /// </summary>
    /// <param name="logCommands">Journalise chaque commande SQL (catégorie « Queryeables.SQL »).</param>
    /// <param name="factory">Fabrique personnalisée, par exemple une <c>SqliteDbFactory</c>. Par défaut : <see cref="DbFactory{TDbConnection}"/>.</param>
    /// <param name="ignoreNotMapped">
    /// Fait ignorer à Dommel les propriétés [NotMapped]. Ce réglage de Dommel est global : passez false si vous
    /// avez votre propre résolveur, et enveloppez-le dans <see cref="NotMappedPropertyResolver"/>.
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

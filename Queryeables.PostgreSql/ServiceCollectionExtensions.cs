using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Queryeables.Dommel;
using System.Data;
using System.Data.Common;

namespace Queryeables.PostgreSql;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a PostgreSQL connection factory (<see cref="DbProviderFactory"/>),
    /// and a transient <see cref="IDbConnection"/> for code that injects it directly.
    /// </summary>
    /// <remarks>
    /// Dommel recognizes <see cref="NpgsqlConnection"/> and generates PostgreSQL SQL, including with
    /// <paramref name="logCommands"/>: a derived factory is no longer needed.
    /// Dommel quotes table and column names, which makes them case-sensitive.
    /// If the database uses lowercase names, declare them with [Table] and [Column] on the entities.
    /// </remarks>
    public static IServiceCollection UseNpgsqlFactory(this IServiceCollection services,
        string connectionString,
        bool logCommands = false,
        LogLevel commandLogLevel = LogLevel.Information)
    {
        services.UseFactory<NpgsqlConnection>(connectionString, logCommands, commandLogLevel: commandLogLevel);
        services.AddTransient<IDbConnection>(sp => sp.GetRequiredService<DbProviderFactory>().CreateConnection()
            ?? throw new InvalidOperationException("La fabrique n'a pas pu créer de connexion."));
        return services;
    }
}

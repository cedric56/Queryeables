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
    /// Enregistre une fabrique de connexions PostgreSQL (<see cref="DbProviderFactory"/>),
    /// et une <see cref="IDbConnection"/> transitoire pour le code qui l'injecte directement.
    /// </summary>
    /// <remarks>
    /// Dommel reconnaît <see cref="NpgsqlConnection"/> et génère du SQL PostgreSQL, y compris avec
    /// <paramref name="logCommands"/> : une fabrique dérivée n'est plus nécessaire.
    /// Dommel met les noms de tables et de colonnes entre guillemets, ce qui les rend sensibles à la casse.
    /// Si la base utilise des noms en minuscules, déclarez-les avec [Table] et [Column] sur les entités.
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

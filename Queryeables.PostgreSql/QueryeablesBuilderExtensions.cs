using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Queryeables.Abstraction;
using Queryeables.Dommel;

namespace Queryeables.PostgreSql;

public static class QueryeablesBuilderExtensions
{
    /// <summary>Stores with Dommel on PostgreSQL (Npgsql).</summary>
    /// <param name="logCommands">Logs every SQL command (category "Queryeables.SQL"). For development.</param>
    /// <param name="notifications">
    /// The tables whose changes are reported to the monitors, like the SQLite hooks: triggers and <c>LISTEN/NOTIFY</c>,
    /// through a <see cref="PostgresChangeListener"/> registered as a hosted service. Without it, nothing is reported.
    /// </param>
    /// <example>
    /// <code>
    /// q.UseNpgsql(connectionString, notifications: n => n.Watch&lt;Person&gt;(p => p.Id).Watch&lt;Contact&gt;(c => c.Id))
    /// </code>
    /// </example>
    public static QueryeablesBuilder UseNpgsql(this QueryeablesBuilder builder, string connectionString,
        bool logCommands = false, LogLevel commandLogLevel = LogLevel.Information,
        Action<PostgresNotifications>? notifications = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.UseNpgsqlFactory(connectionString, logCommands, commandLogLevel);

        if (notifications is not null)
        {
            var options = new PostgresNotifications();
            notifications(options);
            builder.Services.TryAddSingleton(options);
            builder.Services.TryAddSingleton(sp => new PostgresChangeListener(connectionString, options,
                sp.GetRequiredService<DbMonitorRegistry>(), sp.GetService<ILogger<PostgresChangeListener>>()));
            builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PostgresChangeListener>());
        }

        return builder.UseDommelStores();
    }
}

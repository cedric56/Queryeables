using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables.MongoDB;

public static class QueryeablesBuilderExtensions
{
    /// <summary>Stores on MongoDB: <see cref="MongoEntityStore"/>, one collection per entity type.</summary>
    /// <param name="connectionString">For example <c>mongodb://localhost:27017/?replicaSet=rs0</c>; transactions and change
    /// streams need a replica set.</param>
    /// <param name="database">The database name.</param>
    /// <param name="notifications">
    /// The collections whose changes are reported to the change feeds, through a change stream (<see cref="MongoChangeListener"/>,
    /// registered as a hosted service). Without it, nothing is reported.
    /// </param>
    /// <example>
    /// <code>
    /// q.UseMongoDB("mongodb://localhost:27017/?replicaSet=rs0", "shop", notifications: n => n.Watch&lt;Order&gt;().Watch&lt;Customer&gt;())
    /// </code>
    /// </example>
    [RequiresUnreferencedCode(MongoAot.Trimming)]
    [RequiresDynamicCode(MongoAot.DynamicCode)]
    public static QueryeablesBuilder UseMongoDB(this QueryeablesBuilder builder, string connectionString, string database,
        Action<MongoNotifications>? notifications = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        builder.Services.TryAddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
        builder.Services.TryAddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(database));

        if (notifications is not null)
        {
            var options = new MongoNotifications();
            notifications(options);
            builder.Services.TryAddSingleton(options);
            builder.Services.TryAddSingleton(sp => new MongoChangeListener(sp.GetRequiredService<IMongoDatabase>(), options,
                sp.GetRequiredService<ChangeFeedRegistry>(), sp.GetService<ILogger<MongoChangeListener>>()));
            builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<MongoChangeListener>());
        }

        return builder.UseStores(sp => MongoEntityStore.Factory(sp.GetRequiredService<IMongoDatabase>()));
    }
}

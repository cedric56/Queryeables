using Microsoft.AspNetCore.Server.Kestrel.Core;
using Queryeables;
using Queryeables.Abstraction;
using Queryeables.Dommel;
using Queryeables.Generated;
using Queryeables.MagicOnion.Server;
using Queryeables.Remote;
using Queryeables.Sqlite;
using QueryeablesApp.Shared;
using System.Data.Common;

var builder = WebApplication.CreateBuilder(args);

// gRPC over HTTP/2 without TLS, on this machine only: development. In production, listen in HTTPS (and see
// docs/guides/security.md: authentication, then EntityAccessPolicies.Authenticated instead of AllowAll).
builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(5070, listen => listen.Protocols = HttpProtocols.Http2));

var connectionString = $"Data Source={Path.Combine(AppContext.BaseDirectory, "server.db")}";
builder.Services.AddMagicOnion();
builder.Services.UseQueryeables(q => q
    .UseSqlite(connectionString)
    .AddGeneratedEntities()                               // the graph repository: contacts saved with their person
    .AddMagicOnionServer(server => server
        .Expose<Person>(EntityAccessPolicies.AllowAll)    // this machine only
        .Expose<Contact>(EntityAccessPolicies.AllowAll)));

var app = builder.Build();

await DommelEntityStore.EnsureSchemaAsync(app.Services.GetRequiredService<DbProviderFactory>(), connectionString, """
    CREATE TABLE IF NOT EXISTS Person  (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS Contact (Id INTEGER PRIMARY KEY AUTOINCREMENT, PersonId INTEGER NOT NULL, Kind TEXT NOT NULL);
    """);

app.MapMagicOnionService();
app.Run();

/// <summary>The MagicOnion service that serves the exposed entities: MagicOnion finds it by this class.</summary>
public sealed class AppLayerService(EntityRequestDispatcher dispatcher) : MagicOnionService(dispatcher);

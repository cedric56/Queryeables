using QueryeablesApp.Server;

var builder = WebApplication.CreateBuilder(args);

// The tables created from the entities of QueryeablesApp.Shared, every entity published to the clients (their replicas); the
// clients write through PeopleService. Development: gRPC without TLS on this machine only (appsettings.json). On a network,
// listen in HTTPS, authenticate the users and use EntityAccessPolicies.Authenticated (see docs/guides/security.md).
builder.Services.UseQueryeables(q => q
    .UseSqlite($"Data Source={Path.Combine(AppContext.BaseDirectory, "server.db")}")
    .AddGeneratedEntities()
    .CreateTables()
    .AddMagicOnionServer(server => server.ExposeGenerated(EntityAccessPolicies.AllowAll)));

var app = builder.Build();
app.MapQueryeables();
app.MapMagicOnionService<PeopleService>();
app.Run();

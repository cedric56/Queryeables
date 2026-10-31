# Queryeables.Server

Serve Queryeables entities to remote clients from an ASP.NET Core application, over
[MagicOnion](https://github.com/Cysharp/MagicOnion). Targets **.NET 10**.

```bash
dotnet add package Queryeables.Server
```

## Set up

```csharp
// MagicOnion finds the service by this class.
public sealed class AppLayerService(EntityRequestDispatcher dispatcher) : MagicOnionService(dispatcher);

builder.Services.AddMagicOnion();
builder.Services.UseQueryeables(q => q
    .UseSqlite("Data Source=server.db")
    .AddGeneratedEntities()                                    // the graph services: children saved with their parent
    .AddMagicOnionServer(server => server
        .Expose<Person>(EntityAccessPolicies.Authenticated)
        .Expose<Contact>(EntityAccessPolicies.Authenticated)
        .Expose<Product>(EntityAccessPolicies.ReadOnly)));

var app = builder.Build();
app.MapMagicOnionService();
```

Only exposed types can be reached, each with its policy: `Authenticated`, `ReadOnly`, `AllowAll` (tests and closed networks
only), or your own rule with `EntityAccessPolicies.From(request => ...)`. Each call runs with the user authenticated by
ASP.NET Core. Writes are validated (DataAnnotations); remote transactions run in one transaction of the server's stores.
`Expose<T>` fails at startup when `T` has `[EntityGraph]` collections but no graph service is registered.

A policy decides per type. To keep each user to its own rows, give the type a row filter: it is added to every read and
checked on every write.

```csharp
.Expose<Order>(EntityAccessPolicies.Authenticated,
    rows: EntityRowFilter.For<Order, long>(o => o.Id, user => o => o.TenantId == TenantOf(user)))
```

Pages are cut to `maxPageSize` (1000); a read that is not paged fails beyond `maxReadSize` (10,000). See
[security](https://github.com/cedric56/Queryeables/blob/master/docs/guides/security.md).

## Change notifications

```csharp
public sealed class PersonHub(RecordHubRepository<Person> repository) : RecordHub<Person>(repository);

builder.Services.AddSingleton<RecordHubRepository<Person>>();
```

`RecordHubRepository<T>` listens to the monitor of `T` (registered by `AddGeneratedEntities()` for cached types, or by
`q.AddMonitor<T>()`) and broadcasts every change to the subscribed clients, except the one that made it. Put `[Authorize]`
on the hub class when the server requires authentication.

## Security

- Never use `EntityAccessPolicies.AllowAll` on a public network.
- Keep connection strings and certificate passwords out of the source code (user secrets, environment variables).
- No SQL and no expression is received: only "property, operator, value" conditions, at most 1000 per request.

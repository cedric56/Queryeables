# Queryeables.MagicOnion.Server

Serve Queryeables entities to remote clients from an ASP.NET Core application, over
[MagicOnion](https://github.com/Cysharp/MagicOnion). Targets **.NET 10**.

```bash
dotnet add package Queryeables.MagicOnion.Server
```

## Set up

```csharp
builder.Services.UseQueryeables(q => q
    .UseSqlite("Data Source=server.db")
    .AddGeneratedEntities()                                    // the graph services: children saved with their parent
    .AddMagicOnionServer(server => server
        .Expose<Product>(EntityAccessPolicies.AllowAll)        // one type, its own policy
        .ExposeGenerated(EntityAccessPolicies.Authenticated)));   // every other entity the generator knows

var app = builder.Build();
app.MapQueryeables();
```

`AddMagicOnionServer` registers MagicOnion and the JSON entity serializer of the clients; `MapQueryeables()` maps the
service. A server that declares its own class deriving from `MagicOnionService` keeps working with `MapMagicOnionService()`.

Only exposed types can be reached, each with its policy: `Authenticated`, `AllowAll` (tests and closed networks
only), or your own rule with `EntityAccessPolicies.From(request => ...)`. Each call runs with the user authenticated by
ASP.NET Core. The clients read replicas: every row they may read, the changes, a row by key. They write through services
of the application (`app.MapMagicOnionService<PeopleService>()`), which use the stores of the server.

A policy decides per type. To keep each user to its own rows, give the type a row filter: it is added to every read.

```csharp
.Expose<Order>(EntityAccessPolicies.Authenticated,
    rows: RowLevelSecurity.For<Order, long>(o => o.Id, user => o => o.TenantId == TenantOf(user)))
```

A replica fails beyond `maxReadSize` (10,000) rows rather than being cut. See
[security](https://github.com/cedric56/Queryeables/blob/master/docs/guides/security.md).

## Change notifications

```csharp
public sealed class PersonHub(ChangeHubConnections<Person> repository) : ChangeHub<Person>(repository);

builder.Services.AddSingleton<ChangeHubConnections<Person>>();
```

`ChangeHubConnections<T>` listens to the change feed of `T` (registered by `AddGeneratedEntities()` for cached types, or by
`q.AddChangeFeed<T>()`) and broadcasts every change to the subscribed clients, except the one that made it. The hub refuses
anonymous subscriptions: put `[Authorize]` on the hub class and authenticate the server. For public data only, register
`new ChangeHubOptions { AllowAnonymous = true }` as a singleton.

## Security

- Never use `EntityAccessPolicies.AllowAll` on a public network.
- Keep connection strings and certificate passwords out of the source code (user secrets, environment variables).
- No SQL and no expression is received: only "property, operator, value" conditions, at most 1000 per request.

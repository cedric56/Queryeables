# Queryeables.SignalR.Server

The **SignalR** hub that broadcasts a server's changes to [Queryeables](https://github.com/cedric56/Queryeables) clients
([`Queryeables.SignalR`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.SignalR/README.md)). Targets **.NET 10**.

```bash
dotnet add package Queryeables.SignalR.Server
```

## Set up

```csharp
builder.Services.AddSignalR().AddMessagePackProtocol();
builder.Services.AddSingleton<ConnectionTracker<Person>>();

app.MapHub<SignalRHub<Person>>("/hubs/person");
```

One hub per entity type. `ConnectionTracker<T>` listens to the monitor of `T` (registered by `AddGeneratedEntities()` for
cached types, or by `q.AddMonitor<T>()`) and sends every change to the connected clients, except the one that made it.
Broadcasts go through a queue: order is kept, and the database never waits for the network. Require authentication with
`.RequireAuthorization()` on `MapHub` when the data is not public.

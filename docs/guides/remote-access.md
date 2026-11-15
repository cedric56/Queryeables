# Remote access

A server publishes its entity types; each client keeps a **replica** of the rows its user may read, in its caches, kept up to
date by the server's notifications, and queried in memory. A client writes by calling a service of the application, which
uses its own stores on the server. Queries composed by the client and generic writes are not part of the core: the package
`Queryeables.Offline` adds them (with the offline mode), on both sides.

## The layers

| Layer | Client | Server |
| --- | --- | --- |
| Your code | `IPersonCache` (the replica), your services | (the same entities), your services |
| Remote core (`Queryeables`) | `RemoteEntityStore` | `EntityRequestDispatcher`, `EntityHandler<T>` |
| Transport | `IEntityTransport` | forwards to the dispatcher |
| MagicOnion | `Queryeables.MagicOnion.Client` | `Queryeables.MagicOnion.Server` |

The remote core does not depend on MagicOnion: a transport implements `IEntityTransport` on the client and calls the
dispatcher on the server. `dispatcher.AsTransport()` connects both in process, which the sample and the tests use.

## Server

```csharp
builder.Services.UseQueryeables(q => q
    .UseSqlite(builder.Configuration.GetConnectionString("App")!)
    .AddGeneratedEntities()
    .AddMagicOnionServer(server => server
        .Expose<Order>(EntityAccessPolicies.Authenticated,                         // its own policy and row filter
            rows: RowLevelSecurity.For<Order, long>(o => o.Id, user => o => o.OwnerId == user!.FindFirstValue(ClaimTypes.NameIdentifier)))
        .ExposeGenerated(EntityAccessPolicies.Authenticated)));                     // every other generated entity

app.MapQueryeables();
```

`AddMagicOnionServer` registers MagicOnion; `MapQueryeables()` maps the Queryeables service (an application that declares
its own class deriving from `MagicOnionService` can keep it, mapped by `MapMagicOnionService()`). For each exposed type, a
client may read:

- every row its user may read (the policy of the type, then its row filter), at most `maxReadSize` (10 000 by default):
  beyond, the read is refused rather than cut, and the type is read through a service instead;
- the rows changed since a date (types with `[LastModified]`): what a replica downloads after a reconnection;
- a row by its key: what a replica reads again when the server notifies a change. A row that leaves the user's rows
  leaves the replica at that moment.

Anything else (a filter, a page, a write) is answered `NotSupported`.

## Client

```csharp
services.UseQueryeables(q => q
    .UseMagicOnion("https://server:5001")
    .AddGeneratedEntities()
    .WaitForServer());              // at startup, waits (30 s) for a server that is still starting

var orders = app.GetRequiredService<IOrderCache>();
var late = orders.Entities.Where(o => o.Due < DateTime.Today);    // in memory, without a call
```

- **The replica** is the cache of the type (`[EntityCache]`, `IOrderCache`): loaded once (at startup with
  `[EntityCache(Preload = true)]`), then each change the server notifies reads its row again. Query it with LINQ in memory.
- **The store** of a remote client (`RemoteEntityStore`) reads only what a replica needs: every row, the changes, a row by
  key (`e => e.Id == id`). Any other predicate, and every write, throws `EntityStoreException` (`NotSupported`) before any
  call.
- **Errors**: reads throw `EntityStoreException` with the server's error kind.

## Writing from a client

Write through a service of your application: a MagicOnion service, an HTTP endpoint, whatever your application already
uses. It runs on the server, with the stores and graph repositories of the server, under the rules you write; the replicas
follow by the notification of the change, the author's included.

To let the clients write and query through the exposed types themselves (inserts, updates, deletes, graphs, transactions,
filters, pages), add the package `Queryeables.Offline`: `server.AllowWrites()` on the server, `q.UseRemoteWrites()` on the
client. Each write then goes through the access policy, the row filter and validation of its type
([security](security.md)), and the offline mode is available ([offline mode](offline-mode.md)).

## Entities on the wire

The entities travel as JSON (`JsonEntitySerializer`): plain classes, no attribute of the transport. A navigation back to
the parent is written once (a cycle is cut). `[JsonIgnore]` keeps a property on the server (see
[security](security.md#a-value-that-never-leaves-the-server)). Keep the entities in a library referenced by the client and
the server, with the `Queryeables` package only: `AddGeneratedEntities()` registers them on both sides.

- **Native AOT**: the default serializer reads the types by reflection. Pass a `JsonEntitySerializer` built on a
  source-generated `JsonSerializerContext` on both sides: `q.UseMagicOnion(url, new JsonEntitySerializer(AppJson.Default.Options))`
  and `AddMagicOnionServer(serializer, ...)`.
- **MessagePack**, as before: `MessagePackEntitySerializer.Default` on the client and `MessagePackEntitySerializer.Server` on
  the server, with `[MessagePackObject]` on the entities and `[IgnoreMember]` on navigations and hidden values. Smaller
  messages and faster to read and write than JSON: worth it for many rows or a slow network (the
  [benchmarks](../benchmarks.md) measure MessagePack). An entity that still hides a value with `[IgnoreMember]` only, served
  in JSON, stops the server at startup (`MapQueryeables`): give it `[JsonIgnore]` too, or keep MessagePack.

## Limits

- A replica holds at most `maxReadSize` rows of a type for a user: a larger type is read through a service.
- Reads are authorized per type by a policy, and per row by a row filter (see [security](security.md#row-filters)).
- Without `Queryeables.Offline`, a client has no generic write and no query composed for the server: that is the point.

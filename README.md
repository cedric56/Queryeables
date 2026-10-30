# Queryeables

[![CI](https://github.com/cedric56/Queryeables/actions/workflows/ci.yml/badge.svg)](https://github.com/cedric56/Queryeables/actions/workflows/ci.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
[![MIT License](https://img.shields.io/badge/license-MIT-5B35E0)](LICENSE.txt)

**One data-access API for .NET, local or remote: your entities, their caches and their children, the same code everywhere.**

A view model or a service asks for `IPersonService` or `IEntityCache<Person, long>`. It never knows whether the data comes
from SQLite through Dommel, from SQL Server through EF Core, or from a server across the network.

> **Status:** early. Packages are not published on NuGet yet, and the API may still change.

## In one minute

```csharp
[EntityCache]                                            // an in-memory cache that follows the database
public class Person
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    [EntityGraph(ParentNavigation = nameof(Contact.Owner))]   // saved, removed and loaded with the person
    public List<Contact>? Contacts { get; set; }          // null = not loaded: saving leaves the contacts alone
}

public class Contact
{
    public long Id { get; set; }
    public long PersonId { get; set; }                   // the foreign key, found by its name
    public Person? Owner { get; set; }
}

services.UseQueryeables(q => q
    .UseSqlite("Data Source=app.db")                     // or UseDommel, UseNpgsql, UseEntityFramework, UseMagicOnion
    .AddGeneratedEntities());                            // what the attributes describe, written by the source generator
```

```csharp
public sealed class PersonsViewModel(IPersonService persons, IEntityCache<Person, long> cache)
{
    public IReadOnlyCollection<Person> All => cache.Entities;               // kept up to date by the database
    public Task<DbResult> SaveAsync(Person person) => persons.SaveAsync(person);   // the person and its contacts, one transaction
}
```

Two attributes, one registration call. To serve the same entities to remote clients, the client changes one line:
`UseMagicOnion("https://server:5001")` instead of `UseSqlite(...)`.

The [sample](Samples/Queryeables.Sample/Program.cs) runs this end to end on SQLite.

## Start with your provider

| Data | Package | Setup |
| --- | --- | --- |
| SQLite | `Queryeables.Sqlite` | `q.UseSqlite("Data Source=app.db")`: changes are reported to the caches by SQLite itself |
| PostgreSQL | `Queryeables.PostgreSql` | `q.UseNpgsql(connectionString)` |
| SQL Server, MySQL... (Dommel) | `Queryeables.Dommel` | `q.UseDommel<SqlConnection>(connectionString)` |
| Any EF Core provider | `Queryeables.EntityFramework` | `q.UseEntityFramework(sp => new AppDbContext(options))` |
| A Queryeables server | `Queryeables.Onion.Client` | `q.UseMagicOnion("https://server:5001")` |
| Your own store | `Queryeables` | `q.UseStores(sp => new MyStore(...))` |

Each provider brings the `Queryeables` package: the contracts, the remote-access core, the setup and the source generator.

## What you get

- **One store API**, `IEntityStore`: async CRUD, filters, paging, counting and transactions, identical for Dommel, EF Core
  and a remote server. Every implementation passes the same [contract tests](Queryeables.Tests/Contract).
- **Entity graphs**, `[EntityGraph]`: a parent saved, removed and loaded with its children, navigation properties set,
  related entities found in their caches. The generator writes `I{Parent}Service` and `{Parent}Graph`.
- **Caches that follow the database**, `[EntityCache]`: row by row, bursts coalesced, incremental resynchronization after
  a reconnection.
- **Transactions that report their outcome**: `DbResult` with an error kind (`Validation`, `Conflict`, `NotFound`,
  `Forbidden`...) instead of an exception.
- **Remote access without coupling**: the remote core speaks to an `IEntityRemote`; MagicOnion is one transport among
  others. No SQL from the client, a whitelist of filter operators, an access policy per type.
- **Change notifications** from SQLite hooks or SQL Server, relayed to clients over MagicOnion or SignalR, without echoing a
  client's own changes.
- **Sync conventions**: `[LastModified]`, `[SoftDelete]`, optimistic concurrency with `[ConcurrencyCheck]`.
- **Native AOT and trimming analyzers** on every library; the generator replaces reflection-based registration.

## Entity graphs

```csharp
public class Person
{
    public long Id { get; set; }

    // Each contact belongs to a person (IdContact) and points to another person (IdPerson).
    [EntityGraph(ForeignKey = nameof(Contact.IdContact),          // by default Id{Parent} or {Parent}Id
                 ParentNavigation = nameof(Contact.Owner),         // set to the parent after loading
                 LookupNavigation = nameof(Contact.Person),        // set to the entity found in its cache
                 LookupForeignKey = nameof(Contact.IdPerson))]     // by default Id{Navigation} or {Navigation}Id
    public List<Contact>? Contacts { get; set; }
}
```

A null collection means "not loaded": `SaveAsync` leaves the stored children alone. An empty list means "no children": they
are deleted. Do not initialize the collection (`= []`): a person read from a cache would then delete its contacts when
saved. An existing child sent with another parent's key is rejected (Conflict), so that a client cannot move it.

`AddGeneratedEntities()` registers:

| Service | What it does |
| --- | --- |
| `IPersonService` (also `IDbTransaction<Person>`) | `SaveAsync`: inserts or updates the person, writes its key into the contacts, then deletes, inserts and updates contacts to match the list, in one transaction (a null list is left alone). `RemoveAsync`: the contacts, then the person. `LoadContactsAsync`: reads the contacts only and sets their navigation properties; `LoadChildrenAsync` reads every collection. |
| `IPersonGraph` | Sets the navigation properties; it receives the cache of each looked-up type. |
| `IEntityCache<T, TKey>` | For every `[EntityCache]` type and every looked-up type. |

A mistake in the attributes is a compile error with a clear message (QRY001 to QRY009), not an exception at run time.
Without the generator, `q.AddCache<T, TKey>(key)` and `q.AddGraph(...)` do the same by hand
(`EntityGraph.For<Person, long>(p => p.Id).HasMany(...)`).

## Transactions and DbResult

```csharp
await using var store = stores.Create();      // IEntityStoreFactory, registered by the provider
DbResult result = await store.RunInTransactionAsync(async s =>
{
    await s.SaveAsync(order, o => o.Id);       // insert when the key is 0, update otherwise
    await s.SyncChildrenAsync(previousLines, order.Lines, l => l.Id == 0, (a, b) => a.Id == b.Id);
});

if (!result.Successful)
    Show(result.ErrorCode, result.Exception, result.Errors);
```

Exceptions inside the work and failed commits become a failed `DbResult`; cancellation still throws.
`.OrThrow()` turns a failure back into an `EntityStoreException`. Register an `IEntityStoreFactory`, never a store: a store
is one connection, disposed by its user.

## Remote access

The remote core (`Queryeables.Remote`, inside the `Queryeables` package) does not depend on any transport:

- the client store, `RemoteEntityStore`, talks to an **`IEntityRemote`** (one method per operation, entities as bytes);
- the server, `EntityRequestDispatcher`, receives the same calls and routes them to the exposed types;
- an **`IEntitySerializer`** turns entities into bytes.

A transport only implements `IEntityRemote` on the client and forwards to the dispatcher on the server. MagicOnion is the
one shipped (`Queryeables.Onion`, `.Onion.Client`, `.Server`, with MessagePack); `dispatcher.AsRemote()` connects both sides
in-process, for tests or an application that hosts its own server.

**Server** (ASP.NET Core with MagicOnion):

```csharp
public sealed class AppLayerService(EntityRequestDispatcher dispatcher) : MagicOnionService(dispatcher);

builder.Services.AddMagicOnion();
builder.Services.UseQueryeables(q => q
    .UseSqlite("Data Source=server.db")
    .AddGeneratedEntities()
    .AddMagicOnionServer(server => server
        .Expose<Person>(EntityAccessPolicies.Authenticated)      // writes go through IPersonService: contacts included
        .Expose<Contact>(EntityAccessPolicies.Authenticated)));

app.MapMagicOnionService();
```

**Client**: `q.UseMagicOnion("https://server:5001")`, and the same `AddGeneratedEntities()`. The generated services send
the parent with its children in one call; the server saves them with its own graph service.

Supported remote predicates: `==`, `!=`, `<`, `<=`, `>`, `>=`, booleans, `ids.Contains(p.Id)`, and `StartsWith`,
`EndsWith`, `Contains` on strings, all combined with `&&` or all with `||`. Anything else throws `NotSupportedException`
before any network call: write a dedicated server method for it. In a remote transaction, writes are queued and sent in
one call on commit: all of them are applied, or none.

## Change notifications

`UseSqlite` reports every row changed through Queryeables to the monitors; for SQL Server,
`monitor.AddTableDependency<Person, long>(connectionString, p => p.Id, out var stop)`. Caches registered by the generator
listen to their monitor.

Remote clients receive the server's changes through a hub, MagicOnion or SignalR:

```csharp
var monitor = provider.GetRequiredService<IDbMonitor<Person>>();
var unsubscribe = await monitor.AddStreamingHub(serverUrl, provider.GetRequiredService<IDbSession>());
```

Notifications are a signal to refresh, not a durable log: after a reconnection, call `cache.SyncAsync()`, which reads only
the rows changed in the meantime (types with `[LastModified]`).

## Packages

| Project | Contents |
| --- | --- |
| `Queryeables` (Abstraction, Remote, Core, Generated) | Contracts, remote core, `UseQueryeables`, source generator. No database, no transport. |
| `Queryeables.Dommel` | `DommelEntityStore`, connection factory, SQL logging |
| `Queryeables.Sqlite`, `Queryeables.PostgreSql` | Dommel on SQLite (with change reporting), on PostgreSQL |
| `Queryeables.EntityFramework` | `EfEntityStore` |
| `Queryeables.SqlDependency` | SQL Server change notifications |
| `Queryeables.Onion`, `.Onion.Client`, `.Server` | The MagicOnion transport: messages, client, ASP.NET Core service and hub |
| `Queryeables.SignalR`, `.SignalR.Server` | Change notifications over SignalR |

## Security

- **Never expose `EntityAccessPolicies.AllowAll` on a public network.** Configure ASP.NET Core authentication and use
  `Authenticated`, `ReadOnly`, or `EntityAccessPolicies.From(request => ...)`.
- Keep connection strings and certificate passwords out of the source code.
- No SQL and no expression goes over the wire: only "property operator value" conditions on exposed types.

## Native AOT

Every library sets `IsAotCompatible` and builds without trimming or AOT warnings. At run time, EF Core and Dapper/Dommel
generate code (fine with trimming, not with full Native AOT), MagicOnion clients need the MagicOnion source generator, and
entities sent over MagicOnion need `[MessagePackObject]`.

## Build and test

```bash
dotnet test Queryeables.slnx
dotnet run --project Samples/Queryeables.Sample
dotnet pack Queryeables.slnx -c Release -o ./artifacts -p:Version=0.1.0
```

Design ideas come from the Datasync Community Toolkit, ActualLab.Fusion, ABP and Breeze: see [CREDITS.md](CREDITS.md).
Released under the [MIT License](LICENSE.txt).

# Queryeables

[![CI](https://github.com/cedric56/Queryeables/actions/workflows/ci.yml/badge.svg)](https://github.com/cedric56/Queryeables/actions/workflows/ci.yml)

**One data-access API for .NET, local or remote, with change notifications and caching.**

Queryeables puts Dommel, Entity Framework Core and a remote MagicOnion server behind the same
`IEntityStore` interface. On top of it, it provides transactions that report their outcome, aggregates
(a parent saved with its children), an in-memory cache that follows database changes, and the plumbing that
relays those changes to connected clients over MagicOnion or SignalR.

> **Status:** early. Targets .NET 10. Packages are not published on NuGet yet, and the API may still change.

## Contents

- [Features](#features)
- [Projects](#projects)
- [Getting started](#getting-started)
- [Core concepts](#core-concepts)
  - [IEntityStore](#ientitystore)
  - [Transactions and DbResult](#transactions-and-dbresult)
  - [Aggregates](#aggregates)
  - [Sync conventions](#sync-conventions)
  - [Change notifications and EntityCache](#change-notifications-and-entitycache)
- [Remote access with MagicOnion](#remote-access-with-magiconion)
- [Security](#security)
- [Native AOT](#native-aot)
- [Building, testing and packaging](#building-testing-and-packaging)
- [Credits](#credits) · [License](#license)

## Features

- **One store API**: async CRUD, filtering, paging, counting and transactions, identical for Dommel, EF Core and a
  remote server. Every implementation passes the same contract test suite.
- **Transactions that report their outcome**: `RunInTransactionAsync` returns a `DbResult` with an error kind
  (`Validation`, `Conflict`, `NotFound`, `Forbidden`...) instead of throwing.
- **Aggregates**: describe a parent and its children once; save, remove and load them together with any store.
- **Sync conventions**: `[LastModified]`, `[SoftDelete]` and optimistic concurrency with `[ConcurrencyCheck]`.
- **Change notifications**: SQLite update hooks and SQL Server (SqlTableDependency) feed monitors; MagicOnion and
  SignalR hubs relay the changes to clients, never echoing a client's own changes back to it.
- **EntityCache**: thread-safe, key-indexed in-memory cache that updates row by row, coalesces bursts of
  notifications and resynchronizes incrementally after a reconnection.
- **Controlled remote access**: no SQL from clients, a whitelist of filter operators, a mandatory access policy per
  entity type, server-side validation and a page-size cap.
- **Trimming and AOT analyzers** enabled on every library.

## Projects

| Project | Purpose | Main dependency |
|---|---|---|
| `Queryeables.Abstraction` | `IEntityStore`, `DbResult`, aggregates, `EntityCache`, monitors, sync conventions | none |
| `Queryeables.Dommel` | `DommelEntityStore`, connection factory, SQL logging, DI registration | Dommel |
| `Queryeables.EntityFramework` | `EfEntityStore` | EF Core (relational) |
| `Queryeables.Sqlite` | SQLite connection factory that reports every modified row | Microsoft.Data.Sqlite |
| `Queryeables.PostgreSql` | PostgreSQL connection factory | Npgsql |
| `Queryeables.SqlDependency` | SQL Server change notifications | SqlTableDependency |
| `Queryeables.Onion` | Shared MagicOnion contracts, server-side handlers, `MagicOnionEntityStore` (client store) | MagicOnion.Abstractions |
| `Queryeables.Onion.Client` | gRPC channel and change-hub subscription for clients | MagicOnion.Client |
| `Queryeables.Server` | MagicOnion entity service and change hub for ASP.NET Core | MagicOnion.Server |
| `Queryeables.SignalR` | SignalR client for change notifications | SignalR client |
| `Queryeables.SignalR.Server` | SignalR hub for change notifications | ASP.NET Core |
| `Queryeables.Tests` | Contract and unit tests | xUnit |

Reference only what you need: a local EF Core application needs `Abstraction` and `EntityFramework`, nothing else.

## Getting started

Requirements: the .NET 10 SDK.

```bash
git clone https://github.com/cedric56/Queryeables.git
cd Queryeables
dotnet test Queryeables.slnx
```

Until packages are published, reference the projects, or build local packages:

```bash
dotnet pack Queryeables.slnx -c Release -o ~/packages/queryeables -p:Version=0.1.0
dotnet nuget add source ~/packages/queryeables -n Queryeables-local
```

A minimal local setup with SQLite and Dommel:

```csharp
services.UseSqliteFactory("Data Source=app.db");   // registers DbProviderFactory, DbMonitorRegistry, IDbSession

// later, from a DbProviderFactory resolved from DI
await using var store = DommelEntityStore.Create(factory);
await store.InsertAsync(person);                    // the generated key is written back into person
var adults = await store.SelectAsync<Person>(p => p.Age >= 18);
```

`UseFactory<TConnection>(connectionString)` and `UseNpgsqlFactory(connectionString)` do the same for other providers.
They also make Dommel ignore `[NotMapped]` properties.

## Core concepts

### IEntityStore

One instance is one unit of work (a connection or a `DbContext`). Write application code against the interface:

```csharp
IEntityStore store = DommelEntityStore.Create(factory);           // Dommel
IEntityStore store = new EfEntityStore(new AppDbContext(options)); // EF Core
IEntityStore store = new MagicOnionEntityStore(layer, session);    // remote server

var all    = await store.GetAllAsync<Person>();
var some   = await store.SelectAsync<Person>(p => p.Active && p.CityId == cityId);
var first  = await store.FirstOrDefaultAsync<Person>(p => p.Id == id);
var page   = await store.SelectPagedAsync<Person>(p => p.Active, pageNumber: 1, pageSize: 50);
var count  = await store.CountAsync<Person>(p => p.Active);

await store.InsertAsync(person);
bool updated = await store.UpdateAsync(person);
bool deleted = await store.DeleteAsync(person);
await store.SaveAsync(person, p => p.Id);   // insert when the key is 0, update otherwise
```

Helpers: `GetAsync` (throws `NotFound` instead of returning null), `InsertManyAsync`, `UpdateManyAsync`,
`DeleteManyAsync`, `SyncChildrenAsync`. To add a store for another framework, implement `IEntityStore` and derive a
test class from `EntityStoreContractTests`.

### Transactions and DbResult

```csharp
DbResult result = await store.RunInTransactionAsync(async s =>
{
    await s.UpdateAsync(order);
    await s.SyncChildrenAsync(previousLines, order.Lines, l => l.Id == 0, (a, b) => a.Id == b.Id);
});

if (!result.Successful)
    ShowError(result.ErrorCode, result.Exception, result.Errors);
```

The transaction is committed when the work succeeds, rolled back otherwise. Exceptions thrown inside the work and
failed commits become a failed `DbResult`; cancellation still throws. `RunInTransactionAsync<T>` returns a value
(`DbResult<T>`), and `.OrThrow()` turns a failure back into an `EntityStoreException`.

Application services that save and remove an entity implement `IDbTransaction<T>`:

```csharp
public interface IPersonService : IDbTransaction<Person>   // SaveAsync / RemoveAsync returning DbResult
{
    Task LoadContactsAsync(Person person, CancellationToken ct = default);
}
```

### Aggregates

Describe a parent and its children once, then let `AggregateService` save, remove and load them:

```csharp
var persons = Aggregate.For<Person, long>(p => p.Id)
    .HasMany(p => p.Contacts, foreignKey: c => c.PersonId, key: c => c.Id,
             onLoaded: (person, contacts) => { /* set navigation properties */ });

public sealed class PersonService(Func<IEntityStore> stores, Aggregate<Person, long> persons, bool childrenSavedByServer = false)
    : AggregateService<Person, long>(stores, persons, childrenSavedByServer), IPersonService
{
    public Task LoadContactsAsync(Person person, CancellationToken ct = default) => LoadChildrenAsync(person, ct);
}
```

- **Save**: inserts or updates the parent, writes its key into each child's foreign key, then deletes, inserts and
  updates children to match the collection, in one transaction.
- **Remove**: deletes the children, then the parent.
- **Load**: reads the children into the collection and calls `onLoaded`.

The same service works locally (`() => DommelEntityStore.Create(factory)`) and remotely
(`() => new MagicOnionEntityStore(layer, session)` with `childrenSavedByServer: true`, see below).

### Sync conventions

```csharp
public class Person
{
    public long Id { get; set; }
    [LastModified] public DateTime LastModified { get; set; }
    [SoftDelete] public bool Deleted { get; set; }
    [ConcurrencyCheck] public long Version { get; set; }
}

IEntityStore store = DommelEntityStore.Create(factory).WithSyncConventions();
```

`WithSyncConventions()` stamps `[LastModified]` on every write, turns deletes into soft deletes and hides
soft-deleted rows from reads. `SelectChangesAsync(since)` still returns them, for synchronization.
A stale `[ConcurrencyCheck]` version fails with `EntityErrorCode.Conflict` on every store.

### Change notifications and EntityCache

A `DbMonitor<T>` receives the changes of one table. Change sources publish to a `DbMonitorRegistry`:

- **SQLite**: `UseSqliteFactory` hooks every connection it creates and reports each modified row after the commit.
- **SQL Server**: `monitor.AddTableDependency<Person, long>(connectionString, p => p.Id, out var stop)`.

`EntityCache` keeps an in-memory copy that follows the monitor:

```csharp
var monitor = new DbMonitor<Person>();
registry.Add(monitor);   // DbMonitorRegistry from DI

var cache = new EntityCache<Person, long>(
    () => DommelEntityStore.Create(factory),
    p => p.Id, monitor, RecordKey.Parse<long>);
await cache.LoadAsync();

Person? ana = cache.Find(42);
cache.EntitiesChanged += (_, _) => Refresh();
```

Changed rows are read again one by one; a burst of notifications triggers a single update per row. After a
reconnection, `cache.SyncAsync()` reads only the rows changed in the meantime (types with `[LastModified]`).

Clients receive the server's changes through a hub:

```csharp
var unsubscribe = await monitor.AddStreamingHub(serverUrl, session);   // MagicOnion
var unsubscribe = await monitor.AddSignalR(hubUrl, session);           // or SignalR
```

The session id travels with every write, so a client is not notified of its own changes.

## Remote access with MagicOnion

### Server

```csharp
public sealed class AppLayerService(EntityRequestDispatcher dispatcher) : MagicOnionService(dispatcher);
public sealed class PersonRecordHub(RecordHubRepository<Person> repository) : RecordHub<Person>(repository);
```

```csharp
builder.Services.AddMagicOnion();
builder.Services.UseSqliteFactory($"Data Source={dbPath}");
builder.Services.AddSingleton<IDbMonitor<Person>>(new DbMonitor<Person>());
builder.Services.AddSingleton<RecordHubRepository<Person>>();   // relays database changes to MagicOnion clients

builder.Services.AddSingleton<IEntityHandlerRegistry>(sp =>
{
    var factory = sp.GetRequiredService<DbProviderFactory>();
    Func<IEntityStore> store = () => DommelEntityStore.Create(factory);
    var policy = EntityAccessPolicies.Authenticated;

    var registry = new EntityHandlerRegistry();
    registry.Register(new EntityHandler<Person>(store, policy,
        aggregate: new AggregateService<Person, long>(store, persons)));   // saves contacts with the person
    registry.Register(new EntityHandler<Contact>(store, policy));
    return registry;
});
builder.Services.AddSingleton(sp => new EntityRequestDispatcher(
    sp.GetRequiredService<IEntityHandlerRegistry>(),
    () => DommelEntityStore.Create(sp.GetRequiredService<DbProviderFactory>())));   // enables remote transactions

app.MapMagicOnionService();
```

Only registered types are reachable. Each write goes through the access policy, then DataAnnotations validation.

### Client

```csharp
services.AddSingleton<IDbSession, DbSession>();
services.AddSingleton(sp => sp.Create<IMagicOnionLayer>(serverUrl));   // gRPC through the WebSocket bridge

await using var store = new MagicOnionEntityStore(layer, session);
var adults = await store.SelectAsync<Person>(p => p.Age >= 18);   // same API as locally
```

Supported predicates: `==`, `!=`, `<`, `<=`, `>`, `>=`, boolean properties, `ids.Contains(p.Id)`, and
`StartsWith`, `EndsWith`, `Contains` on strings, all combined with `&&` or all with `||`. Anything else throws
`NotSupportedException` before any network call. During a transaction, writes are queued and sent in one call on
commit: all of them are applied, or none.

## Security

- **Never expose `EntityAccessPolicies.AllowAll` on a public network.** Configure ASP.NET Core authentication and use
  `Authenticated`, `ReadOnly`, or a custom rule with `EntityAccessPolicies.From(request => ...)`.
- Keep connection strings and certificate passwords out of the source code (user secrets, environment variables,
  a vault).
- Restrict CORS to the origins that actually need it.
- Remote filters are a whitelist: no SQL and no arbitrary expression is executed on the server.
- Notifications are a signal to refresh, not a durable event log: after a reconnection, resynchronize
  (`EntityCache.SyncAsync`).

## Native AOT

Every library sets `IsAotCompatible` and builds without trimming or AOT warnings; entity type parameters are
annotated so that their public properties are kept. At run time:

- EF Core and Dapper/Dommel generate code at run time: fine with trimming, not with full Native AOT.
- MagicOnion clients need the MagicOnion source generator under Native AOT.
- Entities sent over MagicOnion need `[MessagePackObject]` for source-generated formatters.

## Building, testing and packaging

```bash
dotnet build Queryeables.slnx
dotnet test Queryeables.slnx
dotnet pack Queryeables.slnx -c Release -o ./artifacts -p:Version=0.1.0
```

The contract tests run every store (Dommel, EF Core and the remote MagicOnion store, with and without sync
conventions) against an in-memory SQLite database. The unit tests cover filters, keys, cache, aggregates,
transactions and change propagation without a database.

The [CI workflow](.github/workflows/ci.yml) builds, tests and packs on every push and pull request. Pushing a tag such
as `v0.1.0` publishes the packages to nuget.org (requires the `NUGET_API_KEY` repository secret).

## Credits

Design ideas come from the Datasync Community Toolkit, ActualLab.Fusion, ABP and Breeze; see [CREDITS.md](CREDITS.md).

## License

[MIT](LICENSE.txt)

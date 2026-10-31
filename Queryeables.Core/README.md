# Queryeables

The core package of [Queryeables](https://github.com/cedric56/Queryeables): one data-access API for .NET, local or remote.
It holds the contracts (`IEntityStore`, `EntityResult`, caches, graphs), the remote-access core, the `UseQueryeables` setup
and the source generator. It has no database and no transport: add a provider package to use it. Targets **.NET 10**.

```bash
dotnet add package Queryeables.Sqlite   # or Queryeables.Dommel, .PostgreSql, .EntityFramework, .Onion.Client
```

Every provider brings this package: you rarely reference it directly, except to write your own store.

## Set up

```csharp
services.UseQueryeables(q => q
    .UseSqlite("Data Source=app.db")      // the provider: where the stores come from
    .WithSyncConventions()                // optional: [LastModified] and [SoftDelete]
    .AddGeneratedEntities());             // what [EntityCache] and [EntityGraph] describe
```

| Builder method | What it registers |
| --- | --- |
| `UseStores(sp => store, isRemote)` | Your own `IEntityStore`, through an `IEntityStoreFactory` |
| `WithSyncConventions()` | `SyncConventionsDecorator` around every store |
| `AddGeneratedEntities()` | The caches, graphs and services written by the generator, for this project and the projects it references |
| `AddChangeFeed<T>(table)` | `IChangeFeed<T>`, fed by the database changes (table name from `[Table]` by default) |
| `AddCache<T, TKey>(e => e.Id)` | `IEntityCache<T, TKey>` and its change feed |
| `AddGraph<TParent, TKey>(sp => graph)` | An `EntityGraph` and its `IAggregateRepository<TParent>` service |
| `AddEntityServer(serializer, s => s.Expose<T>(policy))` | The server side of remote access (`EntityRequestDispatcher`) |

`UseQueryeables` also registers `ChangeFeedRegistry` and `IChangeSession`, and throws at startup when no provider was called.

## Stores

`IEntityStore` is one unit of work (one connection, one `DbContext`, one remote session). Ask the `IEntityStoreFactory`
for one, and dispose it:

```csharp
await using var store = stores.Create();
var adults = await store.SelectAsync<Person>(p => p.Age >= 18);
var page   = await store.SelectPagedAsync<Person>(p => p.Name.StartsWith("A"), pageNumber: 1, pageSize: 50);

EntityResult result = await store.RunInTransactionAsync(async s =>
{
    await s.SaveAsync(order, o => o.Id);   // insert when the key is 0, update otherwise
    await s.UpdateAsync(customer);
});
```

Extensions: `SaveAsync`, `GetAsync` (throws NotFound), `InsertManyAsync`, `UpdateManyAsync`, `DeleteManyAsync`,
`SyncChildrenAsync`. Transactions never throw, except on cancellation: they return a `EntityResult` with an `ErrorCode`
(`Validation`, `Conflict`, `NotFound`, `Forbidden`...). `.OrThrow()` turns a failure into an `EntityStoreException`.

## Entities

```csharp
[EntityCache]                                      // IEntityCache<Person, long>, kept up to date by the database
public class Person
{
    public long Id { get; set; }

    [EntityGraph(ParentNavigation = nameof(Contact.Owner))]
    public List<Contact>? Contacts { get; set; }  // null = not loaded: saving leaves the contacts alone
}
```

The generator writes `IPersonRepository` (save, remove, `LoadContactsAsync`) and `PersonGraph` (navigation properties,
looked-up entities found in their caches). Attribute mistakes are compile errors, QRY001 to QRY009. Entities declared in a
shared library are registered by `AddGeneratedEntities()` in every project that references it.

| Attribute | Effect |
| --- | --- |
| `[EntityCache]` | An in-memory cache, updated row by row (integer keys) or by synchronization |
| `[EntityGraph]` | A child collection saved, removed and loaded with its parent |
| `[LastModified]` | Set on every write; enables incremental synchronization (`cache.SyncAsync()`) |
| `[SoftDelete]` | Delete flags the row; reads hide it; synchronizing clients see the deletion |
| `[ConcurrencyCheck]` | An `int`/`long` version: a stale update or delete is a `Conflict` |
| `[RemoteName]` | The name of the type on the wire |

## Your own store

Implement `IEntityStore` and register it with `q.UseStores(sp => new MyStore(...))`. It must pass the contract tests of
the repository (`Queryeables.Tests/Contract`).

# Tutorial: from a local app to a server, then offline

Four steps on the same application, each building on the one before. Each ends with the guide to read when you need more;
nothing here is required to go on to the next step. Before the packages are published, build them first
([contributing](../CONTRIBUTING.md#trying-the-packages-before-they-are-published)).

## 1. A local application

```bash
dotnet new install Queryeables.Templates
dotnet new queryeables-console -n Contacts
cd Contacts && dotnet run
```

```text
In the cache: Ana
Ana: friend, colleague
Removed: True
```

Three files:

- `Models.cs`: a `Person` and its `Contact`s, plain classes: a table each, named after the class. `[EntityCache]` asks
  for a cache, `[EntityGraph]` says the contacts belong to the person.
- `Program.cs`: `UseSqlite(...)` picks the provider, `AddGeneratedEntities()` registers what the attributes describe
  (the generator wrote it at build time: there is no reflection to configure), `CreateTables()` creates the missing tables.
- `Contacts.csproj`: one package, `Queryeables.Sqlite`. No `using` in the code: the package adds its namespaces.

Every write returns an `EntityResult` (`Successful`, `ErrorCode`, `Exception`) instead of throwing: a conflict or a
validation error is an outcome to show, not a crash. `.OrThrow()` turns a failure into an exception where that is simpler,
and `q.ThrowOnFailure()` does it for every repository.

To read with filters, sorts and pages: `stores.Query<Person>().Where(...).OrderBy(...).Page(2, 25).ToListAsync()`, in
[stores and transactions](guides/stores-and-transactions.md).

## 2. A cache that follows the table

`IPersonCache` holds every person in memory and stays in step with the table. `[EntityCache(Preload = true)]` loads it at
startup. Add this to `Program.cs`:

```csharp
var stores = app.GetRequiredService<IEntityStoreFactory>();
await using (var store = stores.Create())
    await store.SaveAsync(new Person { Name = "Bob" });         // not through the cache

Console.WriteLine($"The cache has it: {string.Join(", ", cache.Entities)}");
```

The cache did nothing: what this application writes reaches its caches as soon as the write (or its transaction's commit)
returns. What others write reaches them too: SQLite reports each change after its commit, and the cache reads that row
again. `cache.EntitiesChanged` tells a screen to refresh (and `EntityCache.Freshness`, whether the last update failed).

A change made by another process (another application, the SQLite shell) is not seen: SQLite reports the changes of
this process only. PostgreSQL, SQL Server and MongoDB report every change: [change notifications](guides/change-notifications.md).
More in [caches and synchronization](guides/caches-and-sync.md).

## 3. A parent with its children

`IPersonRepository` was generated from `[EntityGraph]`: `SaveAsync` writes the person and its contacts in one
transaction, `GetAsync(id)` reads a person with its contacts, `RemoveAsync` deletes them first. What `Contacts` holds
decides what is saved:

```csharp
var ana = (await stores.Query<Person>().Where(p => p.Name == "Ana").ToListAsync()).Single();
// ana.Contacts is null: not loaded. Saving it now leaves its contacts as they are.

await persons.LoadContactsAsync(ana);              // or: ana = await persons.GetAsync(id)
ana.Contacts!.RemoveAll(c => c.Kind == "colleague");
ana.Contacts.Add(new Contact { Kind = "neighbour" });
await persons.SaveAsync(ana);          // one transaction: the colleague deleted, the neighbour inserted, the friend kept
```

`null` leaves the children alone, an empty list deletes them all, a list is what the table will hold. A contact whose key
belongs to another person is refused (`Conflict`). More in [entity graphs](guides/entity-graphs.md).

## 4. Go remote

The same code, with a server between the application and the database:

```bash
dotnet new queryeables-client-server -n Shop
cd Shop
dotnet run --project Shop.Server          # in a first terminal: listens on http://localhost:5070
dotnet run --project Shop.Client          # in a second one
```

```text
On the server: Ana 10:42:07
Ana 10:42:07: friend, colleague
```

- `Shop.Shared` holds `Person` and `Contact`, the same plain classes: they travel as JSON, with no attribute of the
  transport.
- `Shop.Server` is the local application of step 1 plus `AddMagicOnionServer(server => server.AllowWrites().ExposeGenerated(policy))`
  and `app.MapQueryeables()`: only the exposed types can be reached, each with an access policy (`Expose<T>(...)` gives
  one type its own policy and row filter).
- `Shop.Client` registers `UseMagicOnion("http://localhost:5070")` instead of `UseSqlite(...)`, and `WaitForServer()` to
  wait for a server that is still starting. Its caches (`IPersonCache`) are **replicas** of what the server publishes, kept
  up to date by its notifications and queried in memory.
- Writing from the client: the template uses `AllowWrites()` on the server and `UseRemoteWrites()` on the client (the
  package `Queryeables.Offline`), so that `IPersonRepository` saves through the server as it did locally, each graph in one
  transaction. Without that package, a client writes by calling a service of your own on the server
  ([remote access](guides/remote-access.md#writing-from-a-client)).

The template uses `EntityAccessPolicies.AllowAll` and plain HTTP/2 on this machine only (its `appsettings.json`). Before the server leaves your
machine: HTTPS, authentication, `EntityAccessPolicies.Authenticated`, and a row filter when each user has its own rows,
in [security](guides/security.md). Change notifications to the clients are in
[remote access](guides/remote-access.md).

### And offline

A client can keep working without the server: it reads and writes a local copy, and its writes wait in a queue. Add the
offline mode to the client (packages `Queryeables.Offline` and `Queryeables.Offline.Sqlite`):

```csharp
services.UseQueryeables(q => q
    .UseMagicOnion(url)
    .AddGeneratedEntities()
    .WithOfflineMode(o => o
        .UseStorage(new SqliteOfflineStorage($"Data Source={Path.Combine(AppContext.BaseDirectory, "offline.db")}"))
        .AllGeneratedEntities()));                    // or .Entity<Person>(p => p.OwnerId == me) to copy fewer rows
```

Nothing switches by itself: `OfflineMode.StatusChanged` tells the application that the server cannot be reached, the user
chooses to work offline (`GoOfflineAsync`), then to send the queued writes when the server is back (`GoOnlineAsync`), and
decides the conflicts. The code that reads and writes does not change. The scenario with two clients, a network cut and a
conflict is `Samples/Queryeables.OfflineDemo`; the whole story is in [offline mode](guides/offline-mode.md).

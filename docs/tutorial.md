# Tutorial: from a local app to a server, then offline

Four steps on the same application, each building on the one before. Each ends with the guide to read when you need more;
nothing here is required to go on to the next step. Before the packages are published, build them first
([getting started](getting-started.md#before-the-packages-are-published)).

## 1. A local application

```bash
dotnet new install Queryeables.Templates
dotnet new queryeables-console -n Contacts
cd Contacts && dotnet run
```

```text
In the cache: Ana
Ana: friend, colleague
Removed.
```

Three files:

- `Models.cs`: a `Person` and its `Contact`s, plain classes. `[Table]` names the table, `[EntityCache]` asks for a cache,
  `[EntityGraph]` says the contacts belong to the person.
- `Program.cs`: `UseSqlite(...)` picks the provider, `AddGeneratedEntities()` registers what the attributes describe
  (the generator wrote it at build time: there is no reflection to configure).
- `Contacts.csproj`: one package, `Queryeables.Sqlite`.

Every write returns an `EntityResult` (`Successful`, `ErrorCode`, `Exception`) instead of throwing: a conflict or a
validation error is an outcome to show, not a crash.

To read with filters, sorts and pages: `stores.Query<Person>().Where(...).OrderBy(...).Page(2, 25).ToListAsync()`, in
[stores and transactions](guides/stores-and-transactions.md).

## 2. A cache that follows the table

`IEntityCache<Person, long>` holds every person in memory and stays in step with the table: SQLite reports each change
made through Queryeables, whoever made it in the application. Add this after `cache.LoadAsync()`:

```csharp
var stores = provider.GetRequiredService<IEntityStoreFactory>();
await using (var store = stores.Create())
    await store.InsertAsync(new Person { Name = "Bob" });      // not through the cache

await WaitForAsync(() => cache.Entities.Any(p => p.Name == "Bob"));
Console.WriteLine($"The cache saw it: {string.Join(", ", cache.Entities)}");
```

The cache did nothing: the insert was reported after its commit, and the cache read that row again. A burst of changes
costs one read per row; `cache.EntitiesChanged` tells a screen to refresh (and `EntityCache.Freshness`, whether the
last update failed).

A change made by another process (another application, the SQLite shell) is not seen: SQLite reports the changes of
this process only. PostgreSQL, SQL Server and MongoDB report every change: [change notifications](guides/change-notifications.md).
More in [caches and synchronization](guides/caches-and-sync.md).

## 3. A parent with its children

`IPersonRepository` was generated from `[EntityGraph]`: `SaveAsync` writes the person and its contacts in one
transaction, `LoadContactsAsync` reads them back, `RemoveAsync` deletes them first. What `Contacts` holds decides what is
saved:

```csharp
var ana = (await stores.Query<Person>().Where(p => p.Name == "Ana").ToListAsync()).Single();
// ana.Contacts is null: not loaded. Saving it now leaves its contacts as they are.

await persons.LoadContactsAsync(ana);
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

- `Shop.Shared` holds `Person` and `Contact`, with `[MessagePackObject]`: they travel as MessagePack.
- `Shop.Server` is the local application of step 1 plus `AddMagicOnionServer(server => server.Expose<Person>(...))`: only
  the exposed types can be reached, each with an access policy.
- `Shop.Client` registers `UseMagicOnion("http://localhost:5070")` instead of `UseSqlite(...)`, and that is the only
  difference: `IPersonRepository`, `stores.Query<Person>()` and the caches work as before, the server saving each graph in
  one transaction.

The template uses `EntityAccessPolicies.AllowAll` and plain HTTP/2 on this machine only. Before the server leaves your
machine: HTTPS, authentication, `EntityAccessPolicies.Authenticated`, and a row filter when each user has its own rows,
in [security](guides/security.md). Change notifications to the clients are in
[remote access](guides/remote-access.md).

### And offline

A client can keep working without the server: it reads and writes a local copy, and its writes wait in a queue. Add the
offline mode to the client:

```csharp
services.UseQueryeables(q => q
    .UseMagicOnion(url)
    .AddGeneratedEntities()
    .WithOfflineMode(o => o
        .UseStorage(new FileOfflineStorage(Path.Combine(AppContext.BaseDirectory, "offline")))
        .Entity<Person, long>(p => p.Id)
        .Entity<Contact, long>(c => c.Id)));
```

Nothing switches by itself: `OfflineMode.StatusChanged` tells the application that the server cannot be reached, the user
chooses to work offline (`GoOfflineAsync`), then to send the queued writes when the server is back (`GoOnlineAsync`), and
decides the conflicts. The code that reads and writes does not change. The scenario with two clients, a network cut and a
conflict is `Samples/Queryeables.OfflineDemo`; the whole story is in [offline mode](guides/offline-mode.md).

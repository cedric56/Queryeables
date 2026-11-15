# Queryeables.Offline

**Experimental.** The offline mode of [Queryeables](https://github.com/cedric56/Queryeables), and the generic remote writes it
needs, on both sides. Plugged into the core through its extension points: an application without this package carries
nothing of it.

## Server: let the clients write

```csharp
builder.Services.UseQueryeables(q => q
    .UseSqlite("Data Source=server.db")
    .AddGeneratedEntities()
    .AddMagicOnionServer(server => server
        .AllowWrites(durableIdempotency: true)        // writes, filters, pages, batches; results kept in the database
        .ExposeGenerated(EntityAccessPolicies.Authenticated)));
```

Each write goes through the access policy, the row filter and validation of its type; a `[ServerOwned]` value never comes
from a client, and a write sent again after a lost answer is applied once (`StoreIdempotencyStore`, table
`IdempotencyRecords`).

## Client: write through the server

```csharp
services.UseQueryeables(q => q
    .UseMagicOnion("https://server:5001")
    .UseRemoteWrites()                                // IEntityStore writes and queries through the server
    .AddGeneratedEntities());
```

`WritableRemoteEntityStore`: filters, sorted pages, counts, aggregates, projections, patches, writes of many rows,
transactions sent as one batch, graphs saved with their children.

## Client: work offline

```csharp
services.UseQueryeables(q => q
    .UseMagicOnion("https://server:5001")
    .AddGeneratedEntities()
    .WithOfflineMode(o => o
        .UseStorage(new SqliteOfflineStorage("Data Source=offline.db"))   // Queryeables.Offline.Sqlite
        .AllGeneratedEntities()));

await app.Services.GetRequiredService<OfflineMode>().InitializeAsync();
```

`OfflineMode` keeps a local copy of chosen types and a queue of the writes made without the server, tells the application
when the server is lost or back and when the data differs, and the user chooses when to switch and how to decide the
conflicts. Storage: `SqliteOfflineStorage` (`Queryeables.Offline.Sqlite`), `IndexedDbOfflineStorage`
(`Queryeables.Offline.Browser`), `FileOfflineStorage`, `BrowserOfflineStorage` (localStorage), `MemoryOfflineStorage`,
`EncryptedOfflineStorage` over any of them, or your own `IOfflineStorage`. See the
[offline mode guide](https://github.com/cedric56/Queryeables/blob/master/docs/guides/offline-mode.md).

## How it plugs in

| Extension point of the core | What this package plugs in |
| --- | --- |
| `QueryeablesBuilder.DecorateStores` | `WritableRemoteEntityStore` in place of the replica store; `OfflineStoreFactory` in front |
| `EntityServerBuilder.Handlers` | `WritableEntityHandler<T>` in place of the read-only `EntityHandler<T>` |
| `IIdempotencyStore` | `StoreIdempotencyStore` (`durableIdempotency: true`) |
| `IUserScopedData` | The offline copy, forgotten when the user changes; unsent writes refuse the change |
| `QueryeablesDiagnostics.Meter` | `queryeables.offline.sent`, `queryeables.offline.conflicts` |

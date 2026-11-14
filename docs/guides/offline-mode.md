# Offline mode

A remote client can keep working when the server is out of reach: it reads and writes a local copy, and the writes wait
in a queue. **Nothing switches by itself.** The application is told what happens, shows it to the user, and the user
decides when to go offline and when to synchronize.

It runs in Blazor WebAssembly as well: no thread, no blocking wait, and the local copy can be kept in the browser.

## Setup

```csharp
builder.Services.UseQueryeables(q => q
    .UseMagicOnion("https://server:5001")
    .AddGeneratedEntities()
    .WithOfflineMode(o => o
        .UseStorage(new IndexedDbOfflineStorage()) // WebAssembly (Queryeables.Browser); SqliteOfflineStorage on desktop
        .Entity<Person, long>(p => p.Id, where: p => p.OwnerId == session.UserId)   // only what this user needs
        .Entity<Contact, long>(c => c.Id)));       // the children of a graph too
```

`where` limits the copy to the rows the user works on; without it, every row the server gives is copied. Its captured
values are read at each download: when they change (another user signs in), the copy is taken again in full.

`where` is a choice of the client, not a protection: what a user may read is decided on the server, by the access
policy and the row-level security of each type ([security](security.md)). The copy holds what the server lets this user
read, at most.

At startup, before the first store is created, read the stored state (the mode, the copy, the queue):

```csharp
var offline = app.Services.GetRequiredService<OfflineMode>();
await offline.InitializeAsync();
```

`WithOfflineMode` puts an `OfflineStoreFactory` in front of the provider's factory. Services, caches and queries keep
asking for `IEntityStoreFactory`, `IPersonRepository` or `IEntityCache<Person, long>`: they do not change.

## What the user is told

`OfflineMode.StatusChanged` is raised with a reason and the current `OfflineStatus`:

| Reason | When | What the application can offer |
| --- | --- | --- |
| `ServerUnreachable` | A call to the server failed for lack of network | "The server cannot be reached. Work offline?" |
| `ServerAvailable` | The server answers again (checked every `ProbeInterval`) | Offline: "The server is back. Synchronize?" when `Status.IsDesynchronized` |
| `ChangesPending` | A write was queued offline | A badge with `Status.PendingChanges` |
| `ModeChanged` | After `GoOfflineAsync` or `GoOnlineAsync` | Update the banner |
| `SnapshotRefreshed` | After `RefreshSnapshotAsync` | |
| `Synchronized` | After a synchronization, or a conflict resolved | Show `Conflicts` |

`OfflineStatus` holds the mode, whether the server answered last time, the writes waiting, the conflicts, when the copy
was taken, and how many rows changed on the server since (for the types with `[LastModified]`).
`IsDesynchronized` is true when any of the last three is not zero.

```csharp
offline.StatusChanged += (_, e) => InvokeAsync(() =>
{
    banner = e.Reason switch
    {
        OfflineEvent.ServerUnreachable when e.Status.Mode == ConnectionMode.Online => "Server unreachable. Work offline?",
        OfflineEvent.ServerAvailable when e.Status.IsDesynchronized =>
            $"Server back: {e.Status.PendingChanges} change(s) to send, {e.Status.ServerChanges ?? 0} to receive. Synchronize?",
        _ => banner,
    };
    StateHasChanged();
});
```

The event is raised on the thread that noticed the change: a user interface dispatches it to its own.

## The user's choices

```csharp
EntityResult result = await offline.GoOfflineAsync();    // refreshes the copy when the server answers, otherwise uses the stored one
SyncReport report = await offline.GoOnlineAsync();   // sends the queue, takes a fresh copy, back online
await offline.RefreshSnapshotAsync();                // a fresh copy, without changing the mode (refused while writes wait)
```

- `GoOfflineAsync` fails when the server cannot be reached and no copy was ever taken: connect once first.
- `GoOnlineAsync` changes nothing when the server cannot be reached (`report.Completed` is false). If the connection is
  lost in the middle, the writes not sent stay in the queue.

## Writes made offline

- An insert with a default key gets a **temporary key**: negative for numbers, a new `Guid` otherwise. The server gives
  the real key when the row is sent; the copy is then taken again from the server. The writes waiting that refer to it
  (an order created offline for a customer created offline, a child inserted on its own) take the real key before they
  are sent: the foreign keys of the graphs, of `[EntityReference]`, and the properties named `{Type}Id` or `Id{Type}`.
- Writes on the same row are **coalesced**: an insert then updates is sent as one insert, an insert then a delete is not
  sent at all. Only consecutive writes are: a write on another row in between keeps the order (a row it refers to, a
  value it frees), and both writes are sent.
- A parent saved through its graph repository is saved with its children in the copy, and queued as **one** write: the
  server saves the children again when it receives it, as it does online.
- Offline transactions work: a transaction not committed restores the copy and the queue. A transaction is exclusive:
  while it is open, writes through other stores, a synchronization and the resolution of conflicts wait for its end, so
  that its rollback never erases them and nothing half done is sent. Write through the transaction's own store: a write
  through another store of the same flow waits for the transaction, which waits for it.
- The caches follow the copy: each offline write notifies them, and each switch makes them synchronize.

## Conflicts

During `GoOnlineAsync`, each write is sent on its own. A write the server refuses (someone else changed the row, it was
deleted, a validation rule...) does not stop the synchronization: it becomes an `OfflineConflict` in
`report.Conflicts` and `offline.Conflicts`, kept across restarts until the user decides.

- An unexpected failure of the server (`Unknown`: database down, a bug) is not the user's to decide: the synchronization
  stops there (`report.Completed` is false), the write stays queued and goes again at the next one. After three such
  failures in a row, it becomes a conflict.
- A refused insert holds back the writes that depend on it (an update of that row, a row naming it): sent with its
  temporary key, they would fail or point at nothing. They become conflicts too ("Depends on..."); keeping the insert
  ("keep mine") gives them its real key, and they can be kept in turn.

```csharp
foreach (var conflict in offline.Conflicts)
{
    var mine = offline.ReadConflict<Person>(conflict);       // what was written offline
    if (await AskUser(mine, conflict.ErrorCode, conflict.Message))
        await offline.KeepMineAsync(conflict);              // sent again over the server's current row (re-inserted when gone)
    else
        await offline.KeepServerAsync(conflict);            // dropped
}
```

Each write is sent with the id of its queue entry. A write whose answer is lost (the connection drops after the server
committed it), or a client that crashes before recording the answer, sends it again with the same id: the server answers
with the first result instead of applying it twice, so an insert never creates a second row. The server keeps these
results in an `IIdempotencyStore`: in memory for a day by default, which a restart of the server forgets (the server logs
a warning). In production, keep them in the database:

```csharp
.AddEntityServer(serializer, server => { server.DurableIdempotency = true; /* ... */ })
```

`StoreIdempotencyStore` keeps them in the table `IdempotencyRecords` (its creation script is in its documentation), for
30 days, shared by the servers of a farm. A write the server was applying when it stopped is answered with a conflict
when sent again (it may have been applied): the user decides, and "keep mine" sends it again. A write sent again after
the retention is applied again: keep it longer than a client may stay offline. Each answer is recorded as soon as it
arrives.

## What is downloaded and stored

- **Only what changed.** For a type with `[LastModified]` and `[SoftDelete]`, maintained by the server
  (`WithSyncConventions()` on the server), each new copy asks for the rows modified since the latest date already
  received, and removes the rows deleted or now outside `where`. The rows written offline are read again by key, so that
  a write the server refused leaves the server's row in the copy. Other types are downloaded in full each time.
- **Row by row.** The copy is stored one entry per row, the queue one entry per write: a write saves its row and its
  queue entry, and the list of keys only when a row comes or goes.

## Storage

| Storage | Package | Where | For |
| --- | --- | --- | --- |
| `IndexedDbOfflineStorage` | `Queryeables.Browser` | IndexedDB, hundreds of MB | Blazor WebAssembly |
| `SqliteOfflineStorage(connectionString)` | `Queryeables.Sqlite` | One table in a SQLite file, WAL | Desktop, MAUI |
| `FileOfflineStorage(folder)` | `Queryeables` | One file per key, written then renamed | Desktop, when SQLite is not wanted |
| `BrowserOfflineStorage` | `Queryeables` | `localStorage`, about 5 MB per site | Small copies in the browser |
| `MemoryOfflineStorage` | `Queryeables` | Memory, lost on close | Tests; the default |
| Your own `IOfflineStorage` | | | Three methods: read, write, delete bytes |

The copy is written with the application's `IEntitySerializer`. Every storage passes the same contract tests
(`OfflineStorageContractTests`), the browser ones excepted: they need a browser.

### Encryption at rest

None of these storages encrypts by itself: a stolen laptop or a shared browser profile gives the copy away. Wrap the
storage in `EncryptedOfflineStorage` when the data is sensitive:

```csharp
var key = await KeyOfThisUserAsync();      // 32 bytes from the platform's key store: DPAPI, Keychain, Android Keystore...
var storage = new EncryptedOfflineStorage(new SqliteOfflineStorage("Data Source=offline.db"), key);
```

- AES-256-GCM per value; names hashed with HMAC-SHA256, so that type names and row keys do not show either.
- A value bound to its name: moved elsewhere or altered, it fails to decrypt (`CryptographicException`) instead of being
  read. A wrong key fails the same way, rather than silently dropping offline writes.
- The key is the protection: keep it out of the storage and the code, one per user. `EncryptedOfflineStorage.NewKey()`
  makes one.
- On desktop and mobile, wrap the storage as soon as the copy holds personal or business data: it stays on the disk
  after the application closes, until `ForgetAsync` or a change of user.
- Not in Blazor WebAssembly, which has no AES-GCM: encrypt with the Web Crypto API in your own `IOfflineStorage`. In the
  browser, a script injected into the page reads what the page reads, encrypted or not.

## A change of user

The copy, the queue and the caches belong to the signed-in user. When another user signs in on the same device, call
`ChangeUserAsync` once the transport sends the new credentials:

```csharp
await services.ChangeUserAsync();      // IServiceProvider: every cache and the offline mode
```

Everything is forgotten before anything is loaded again, so a failed reload leaves empty data, never the previous
user's. While offline writes wait, the change is refused (`InvalidOperationException`) and nothing is forgotten: send
them (`GoOnlineAsync`) or drop them (`DiscardPendingChangesAsync`) first, so that they are never sent under the next
user's name. The offline mode checks again under its lock, where writes are queued, and is asked before the caches: a
write made during the change refuses it instead of being lost.

## The whole scenario

[`Samples/Queryeables.OfflineDemo`](../../Samples/Queryeables.OfflineDemo/Program.cs) runs it with one server and two
clients in one process, SQLite on both sides: a change that reaches the other client, a network cut, writes on both
sides, the return of the server with what differs, a version conflict, and its resolution. The CI runs it.

## Limitations

- The copy is held in memory while the application runs, and offline queries run on it: keep it to the data a user
  works on with `where`, not millions of rows.
- A full download is one read: beyond the server's `maxReadSize` (10,000 rows by default) it fails. Narrow the copy with
  `where`, or raise the limit for that type on the server.
- Without `[LastModified]` and `[SoftDelete]` on the server, a type is downloaded in full at each copy: a hard delete
  cannot be seen in a list of changes.
- A store keeps the mode it was created in: create a store per unit of work, as services and caches already do.
- A patch, an update or a deletion of many rows made offline is queued as the updates or deletions of its rows: it is
  replayed with their versions, not column by column.

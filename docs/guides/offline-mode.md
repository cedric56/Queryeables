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
        .UseStorage(new BrowserOfflineStorage())   // WebAssembly; new FileOfflineStorage(folder) on desktop
        .Entity<Person, long>(p => p.Id, where: p => p.OwnerId == session.UserId)   // only what this user needs
        .Entity<Contact, long>(c => c.Id)));       // the children of a graph too
```

`where` limits the copy to the rows the user works on; without it, every row the server gives is copied. Its captured
values are read at each download: when they change (another user signs in), the copy is taken again in full.

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
  the real key when the row is sent; the copy is then taken again from the server.
- Writes on the same row are **coalesced**: an insert then updates is sent as one insert, an insert then a delete is not
  sent at all.
- A parent saved through its graph repository is saved with its children in the copy, and queued as **one** write: the
  server saves the children again when it receives it, as it does online.
- Offline transactions work: a transaction not committed restores the copy and the queue.
- The caches follow the copy: each offline write notifies them, and each switch makes them synchronize.

## Conflicts

During `GoOnlineAsync`, each write is sent on its own. A write the server refuses (someone else changed the row, it was
deleted, a validation rule...) does not stop the synchronization: it becomes an `OfflineConflict` in
`report.Conflicts` and `offline.Conflicts`, kept across restarts until the user decides.

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

## What is downloaded and stored

- **Only what changed.** For a type with `[LastModified]` and `[SoftDelete]`, maintained by the server
  (`WithSyncConventions()` on the server), each new copy asks for the rows modified since the latest date already
  received, and removes the rows deleted or now outside `where`. The rows written offline are read again by key, so that
  a write the server refused leaves the server's row in the copy. Other types are downloaded in full each time.
- **Row by row.** The copy is stored one entry per row, the queue one entry per write: a write saves its row and its
  queue entry, and the list of keys only when a row comes or goes.

## Storage

| Storage | Where | For |
| --- | --- | --- |
| `BrowserOfflineStorage` | `localStorage` | Blazor WebAssembly |
| `FileOfflineStorage(folder)` | One file per key, written then renamed | Desktop, MAUI |
| `MemoryOfflineStorage` | Memory, lost on close | Tests; the default |
| Your own `IOfflineStorage` | IndexedDB, SQLite... | Three methods: read, write, delete bytes |

The copy is written with the application's `IEntitySerializer`. `localStorage` holds about 5 MB per site: for larger
copies, implement `IOfflineStorage` over IndexedDB.

## Limitations

- The copy is held in memory while the application runs, and offline queries run on it: keep it to the data a user
  works on with `where`, not millions of rows.
- A full download is one read: beyond the server's `maxReadSize` (10,000 rows by default) it fails. Narrow the copy with
  `where`, or raise the limit for that type on the server.
- Without `[LastModified]` and `[SoftDelete]` on the server, a type is downloaded in full at each copy: a hard delete
  cannot be seen in a list of changes.
- A store keeps the mode it was created in: create a store per unit of work, as services and caches already do.
- A child inserted on its own, under a parent created offline, is sent with the parent's temporary key: save
  aggregates through their repository.

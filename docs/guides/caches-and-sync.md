# Caches and synchronization

`IEntityCache<T, TKey>` is an in-memory copy of a table: `Entities` (a snapshot, safe to enumerate), `Find(key)`, and
`EntitiesChanged`. `[EntityCache]` on the class registers it with its change feed, and as `I{Type}Cache` (`IPersonCache`):
the same cache, without the type of the key. By hand, `q.AddCache<T, TKey>(e => e.Id)`.

```csharp
var cache = provider.GetRequiredService<IPersonCache>();
await cache.LoadAsync();                       // once, at startup; or [EntityCache(Preload = true)]
cache.EntitiesChanged += (_, _) => Refresh(cache.Entities);
```

`[EntityCache(Preload = true)]` loads it when the application starts: by the host (a hosted service), or by
`QueryeablesHost.CreateAsync` / `provider.StartQueryeablesAsync()` in an application without a host.

> **For reference data, not for large tables.** A cache holds every row of its table in memory, on every client. Use it
> for lists a user picks from (countries, products, users of a team), not for orders or logs. Remotely, a load larger than
> the server's `maxReadSize` (10,000 rows by default) fails: query those tables by page instead.

## How it follows the database

What this application writes through its stores (a repository, `InsertAsync`, `UpdateAsync`, `DeleteAsync`, a patch) is in
its caches as soon as the write returns, or as soon as its transaction commits (nothing on a rollback): no notification to
wait for, even on a remote client, which never gets its own back. Writes of many rows at once (`UpdateWhereAsync`,
`DeleteWhereAsync`) and the writes of others come through the notifications.

Each notification of the change feed (`IChangeFeed<T>`) updates the cache:

| The type has | A notification |
| --- | --- |
| An integer key | Reads that row again (or removes it) |
| Another key, and `[LastModified]` | Reads the rows changed since the last one received |
| Another key, no `[LastModified]` | Reloads the table |

A burst of notifications is coalesced: one read per row, or one reload. A failed update raises `RefreshFailed` and keeps
the previous content. `Freshness` says how far to trust it: `NotLoaded`, `Current`, or `Stale` when the last read
failed (`LastError` says why, `LastRefreshedAt` when the last one succeeded), for a screen that shows "last updated at". `Track(entity)` puts an entity you just saved into the cache without waiting for the notification.

Notification sources: SQLite (automatic with `UseSqlite`), SQL Server (`Queryeables.SqlDependency`), or a server's hub
for remote clients (MagicOnion or SignalR). See [Change notifications](change-notifications.md).

## After a disconnection

Notifications are a signal, not a durable log: the ones sent while a client was disconnected are lost. Call
`cache.SyncAsync()` after a reconnection. With `[LastModified]`, it only reads the rows changed meanwhile, soft-deleted
ones included; without it, it reloads the table.

Physically deleted rows are not visible to `SyncAsync`: use `[SoftDelete]` on the types that clients synchronize.

## Timestamps and their limits

Incremental synchronization asks for the rows whose `[LastModified]` is at or after the latest date already received.
Two things can show a row stamped *earlier* than that date after it was read:

- a long transaction: the row is stamped when it is written, but visible only once the transaction commits;
- several servers whose clocks differ, each stamping its own writes.

Each synchronization therefore asks again over a window before that date, `SyncOverlap` (5 seconds by default, on the
cache and on the offline mode). Rows already received come back, which is harmless. A delay longer than the window can
still miss a row: stamp the rows with the database clock (a default or a trigger) rather than the servers', and keep
transactions short. A monotonic version (SQL Server `rowversion`, a sequence) would remove the limit; it is not
supported yet.

# Caches and synchronization

`IEntityCache<T, TKey>` is an in-memory copy of a table: `Entities` (a snapshot, safe to enumerate), `Find(key)`, and
`EntitiesChanged`. `[EntityCache]` on the class registers it with its monitor; by hand, `q.AddCache<T, TKey>(e => e.Id)`.

```csharp
var cache = provider.GetRequiredService<IEntityCache<Person, long>>();
await cache.LoadAsync();                       // once, at startup
cache.EntitiesChanged += (_, _) => Refresh(cache.Entities);
```

## How it follows the database

Each notification of the monitor (`IDbMonitor<T>`) updates the cache:

| The type has | A notification |
| --- | --- |
| An integer key | Reads that row again (or removes it) |
| Another key, and `[LastModified]` | Reads the rows changed since the last one received |
| Another key, no `[LastModified]` | Reloads the table |

A burst of notifications is coalesced: one read per row, or one reload. A failed update raises `RefreshFailed` and keeps
the previous content. `Track(entity)` puts an entity you just saved into the cache without waiting for the notification.

Notification sources: SQLite (automatic with `UseSqlite`), SQL Server (`Queryeables.SqlDependency`), or a server's hub
for remote clients (MagicOnion or SignalR). See [Change notifications](change-notifications.md).

## After a disconnection

Notifications are a signal, not a durable log: the ones sent while a client was disconnected are lost. Call
`cache.SyncAsync()` after a reconnection. With `[LastModified]`, it only reads the rows changed meanwhile, soft-deleted
ones included; without it, it reloads the table.

Physically deleted rows are not visible to `SyncAsync`: use `[SoftDelete]` on the types that clients synchronize.

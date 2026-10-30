# Change notifications

`IDbMonitor<T>` reports the changed rows of a table: `ChangeType` (insert, update, delete), the entity key, and the
connections already aware of the change. Caches listen to it; so can your code (`RecordChanged`).

## Sources

| Source | Setup | Notes |
| --- | --- | --- |
| SQLite | automatic with `q.UseSqlite(...)` | Changes made through the application's connections; reported once the commit has returned, so that a subscriber reading the row sees its new state |
| SQL Server | `monitor.AddTableDependency<T, TKey>(connectionString, e => e.Id, out var stop)` | Service Broker; also sees changes made by other applications |
| A Queryeables server | `monitor.AddStreamingHub(url, session)` (MagicOnion) or `monitor.AddSignalR(url, session)` | The server's changes, without the client's own |
| Your own | `registry.Publish(ChangeType.Update, "Person", key)` | `DbMonitorRegistry` dispatches by table name |

The table name of a monitor is the `[Table]` name of the type, otherwise the type name.

## Server to clients

The server broadcasts the changes of its database to subscribed clients:

```csharp
// MagicOnion
public sealed class PersonHub(RecordHubRepository<Person> repository) : RecordHub<Person>(repository);
builder.Services.AddSingleton<RecordHubRepository<Person>>();

// or SignalR
builder.Services.AddSingleton<ConnectionTracker<Person>>();
app.MapHub<SignalRHub<Person>>("/hubs/person");
```

When a client writes, its notification session travels with the request, so the change reaches every client except its
author. Broadcasts go through a queue: order is kept and the database never waits for the network.

## Reconnection

The SignalR client reconnects by itself; the MagicOnion hub does not (call `AddStreamingHub` again). In both cases,
notifications sent meanwhile are lost: call `cache.SyncAsync()` after a reconnection.

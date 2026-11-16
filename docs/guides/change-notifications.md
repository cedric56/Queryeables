# Change notifications

`IChangeFeed<T>` reports the changed rows of a table: `ChangeType` (insert, update, delete) and the entity key. Caches listen to it; so can your code (`ChangeReceived`).

## Sources

| Source | Setup | Notes |
| --- | --- | --- |
| SQLite | automatic with `q.UseSqlite(...)` | Changes made through the application's connections; reported once the commit has returned, so that a subscriber reading the row sees its new state |
| PostgreSQL | `q.UseNpgsql(cs, notifications: n => n.Watch<Person>(p => p.Id))` | Triggers and `LISTEN/NOTIFY`, delivered at commit; also sees changes made by other applications; resynchronizes after a reconnection |
| MongoDB | `q.UseMongoDB(cs, "app", notifications: n => n.Watch<Person>())` | A change stream (replica set); also sees changes made by other applications; resumes after a reconnection, resynchronizes when it cannot |
| SQL Server | `monitor.AddTableDependency<T, TKey>(connectionString, e => e.Id, out var stop)` | Service Broker; also sees changes made by other applications |
| A Queryeables server | `monitor.AddStreamingHub(url, session)` (MagicOnion) or `monitor.AddSignalR(url, session)` | The server's changes, the client's own writes included |
| Your own | `registry.Publish(ChangeType.Update, "Person", key)` | `ChangeFeedRegistry` dispatches by table name |

The table name of a change feed is the `[Table]` name of the type, otherwise the type name.

## Server to clients

The server broadcasts the changes of its database to subscribed clients:

```csharp
// MagicOnion
public sealed class PersonHub(ChangeHubConnections<Person> repository) : ChangeHub<Person>(repository);
builder.Services.AddSingleton<ChangeHubConnections<Person>>();

// or SignalR
builder.Services.AddSingleton<ConnectionTracker<Person>>();
app.MapHub<SignalRHub<Person>>("/hubs/person");
```

A change reaches every subscribed client, the author of the write included: each replica reads the row again, under its
own row filter. Broadcasts go through a queue: order is kept and the database never waits for the network.

The clients send nothing but their subscription: the server's database is the only source. See
[security](security.md#notification-hubs) for `ChangeHubOptions`. The hubs refuse anonymous connections unless
`ChangeHubOptions.AllowAnonymous` is set: authenticate them (`[Authorize]`, `.RequireAuthorization()`).

## Reconnection

The SignalR client reconnects by itself; the MagicOnion hub does not (call `AddStreamingHub` again). In both cases,
notifications sent meanwhile are lost: call `cache.SyncAsync()` after a reconnection.

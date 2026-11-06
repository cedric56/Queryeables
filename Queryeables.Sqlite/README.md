# Queryeables.Sqlite

[Queryeables](https://github.com/cedric56/Queryeables) on SQLite, with Dommel, **with change notifications**: every row
changed through Queryeables is reported to the change feeds, and the caches follow the database. Targets **.NET 10**.

```bash
dotnet add package Queryeables.Sqlite
```

## Set up

```csharp
services.UseQueryeables(q => q
    .UseSqlite("Data Source=app.db;Foreign Keys=True")
    .AddGeneratedEntities());
```

| Parameter | Default | Effect |
| --- | --- | --- |
| `isTransaction` | `true` | Changes are reported after the commit, and forgotten on rollback. `false`: as soon as a row changes |
| `logCommands`, `commandLogLevel` | `false` | Logs every SQL command |
| `createDb` | | Called once with the factory, for example to create the tables |

## How changes are reported

`SqliteDbFactory` installs SQLite's update, commit and rollback hooks on each connection it opens. A row changed several
times in one transaction is reported once. The commit hook runs before the commit completes, so the changes are reported
only when the store signals that the commit returned (`CommittedWrites`): a cache that reads the row again sees its new
state. A commit made without the store (Dapper on the connection) is reported after `UnsignaledCommitDelay` (50 ms). The client that made the change (the remote caller on a server) travels with
it, so that it is not notified of its own change.

SQLite reports the **rowid**. It is the entity key only for an `INTEGER PRIMARY KEY` table: caches with another key type
resynchronize on each change instead (only the changed rows when the type has `[LastModified]`). Changes made outside the
application's connections (another process, a SQLite tool) are not seen.

## Offline storage

`SqliteOfflineStorage` keeps the copy and the queue of the [offline mode](https://github.com/cedric56/Queryeables/blob/master/docs/guides/offline-mode.md)
in a SQLite file, for desktop and MAUI clients:

```csharp
q.WithOfflineMode(o => o
    .UseStorage(new SqliteOfflineStorage($"Data Source={Path.Combine(FileSystem.AppDataDirectory, "offline.db")}"))
    .Entity<Person, long>(p => p.Id));
```

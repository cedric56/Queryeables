# Queryeables.Sqlite

[Queryeables](https://github.com/cedric56/Queryeables) on SQLite, with Dommel, **with change notifications**: every row
changed through Queryeables is reported to the monitors, and the caches follow the database. Targets **.NET 10**.

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
times in one transaction is reported once. The client that made the change (the remote caller on a server) travels with
it, so that it is not notified of its own change.

SQLite reports the **rowid**. It is the entity key only for an `INTEGER PRIMARY KEY` table: caches with another key type
resynchronize on each change instead (only the changed rows when the type has `[LastModified]`). Changes made outside the
application's connections (another process, a SQLite tool) are not seen.

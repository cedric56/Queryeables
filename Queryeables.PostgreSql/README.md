# Queryeables.PostgreSql

[Queryeables](https://github.com/cedric56/Queryeables) on PostgreSQL, with Dommel and Npgsql. Targets **.NET 10**.

```bash
dotnet add package Queryeables.PostgreSql
```

## Set up

```csharp
services.UseQueryeables(q => q
    .UseNpgsql(connectionString)          // logCommands: true logs the SQL
    .AddGeneratedEntities());
```

`UseNpgsql` also registers a transient `IDbConnection`, for code that injects one directly.

## Names

Dommel quotes table and column names, which makes them case-sensitive in PostgreSQL. If the database uses lowercase names,
declare them with `[Table("person")]` and `[Column("name")]`.

## Change notifications

Like SQLite, PostgreSQL reports changed rows to the monitors, and the caches follow the database. Name the watched tables:

```csharp
services.UseQueryeables(q => q
    .UseNpgsql(connectionString, notifications: n => n
        .Watch<Person>(p => p.Id)
        .Watch<Contact>(c => c.Id))
    .AddGeneratedEntities());
```

- At startup, a trigger is installed on each table (`AFTER INSERT OR UPDATE OR DELETE`), calling `pg_notify` with the
  table, the operation and the key. Without the right to create triggers, set `n.CreateTriggers = false` and run
  `n.TriggerScript()` once with an administrator.
- `PostgresChangeListener`, a hosted service, listens on a dedicated connection and publishes each change. Without a host
  (console application), call `provider.GetRequiredService<PostgresChangeListener>().StartAsync()`.
- PostgreSQL delivers a notification at commit, never for a rolled back transaction: a cache reading the row sees its new
  state. Changes made by any application are reported, not only by this one.
- The listener reconnects by itself (1 s, doubling up to 30 s), then tells the monitors that changes may have been missed:
  the caches synchronize.
- The author of a change is not known to PostgreSQL: the client that made it is notified too (it reads the row again).

The table name is the `[Table]` name of the type, otherwise the type name, as for the monitors; the key column is the
`[Column]` name of the key property, otherwise its name.

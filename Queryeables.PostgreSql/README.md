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

This package does not report changes by itself. Notify the monitors from your own source (`LISTEN/NOTIFY`, logical
replication...) with `DbMonitorRegistry.Publish(type, table, key)`, or let the caches synchronize with `cache.SyncAsync()`
(`[LastModified]`).

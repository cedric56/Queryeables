# Queryeables.SqlDependency

Change notifications from **SQL Server** for [Queryeables](https://github.com/cedric56/Queryeables), through Service Broker
(the `SqlTableDependencyCore` package). Targets **.NET 10**.

```bash
dotnet add package Queryeables.SqlDependency
```

## Use

```csharp
var monitor = provider.GetRequiredService<IChangeFeed<Person>>();
monitor.AddTableDependency<Person, long>(connectionString, p => p.Id, out var stop,
    onError: ex => logger.LogError(ex, "Person change"));
...
stop();
```

Every insert, update and delete of the table is published on the change feed with the entity key, so the caches update the
changed row only. The database needs Service Broker enabled (`ALTER DATABASE ... SET ENABLE_BROKER`) and the user the
rights SqlTableDependency requires.

SQL Server does not tell who made a change: the client that made it is notified of it too.

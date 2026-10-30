# Queryeables.Dommel

[Queryeables](https://github.com/cedric56/Queryeables) stores on [Dommel](https://github.com/henkmollema/Dommel) (Dapper),
for any ADO.NET database: SQL Server, MySQL, SQLite, PostgreSQL... Targets **.NET 10**.

```bash
dotnet add package Queryeables.Dommel
```

For SQLite and PostgreSQL, prefer [`Queryeables.Sqlite`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.Sqlite/README.md) and
[`Queryeables.PostgreSql`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.PostgreSql/README.md), which build on this package.

## Set up

```csharp
services.UseQueryeables(q => q
    .UseDommel<SqlConnection>(connectionString)          // logCommands: true logs the SQL (category "Queryeables.SQL")
    .AddGeneratedEntities());
```

`UseDommel` registers a `DbFactory<TConnection>` as the `DbProviderFactory`, and a `DommelEntityStore` per
`IEntityStoreFactory.Create()`. With your own factory: `services.UseFactory<TConnection>(...)`, then `q.UseDommelStores()`.

## What it adds to Dommel

- **Generated keys written back** into the entity after an insert, like EF Core.
- **Optimistic concurrency**: an `int`/`long` property marked `[ConcurrencyCheck]` is checked and incremented by
  `UPDATE ... WHERE Version = @expected`; a stale entity is a `Conflict`, a vanished one returns false.
- **`[NotMapped]` honored** (`NotMappedPropertyResolver`): navigation properties and graph collections are not columns.
  The resolver is global to Dommel: pass `ignoreNotMapped: false` if you have your own, and wrap it.
- **SQL logging** (`DbLogConnection`): every command with its parameters, at the level you choose. For development.
- `DommelEntityStore.EnsureSchemaAsync(factory, connectionString, script)` runs a creation script.

## Table names

Dommel pluralizes table names (`Person` → `Persons`). Name the table with `[Table("Person")]` when it is singular; change
monitors use the same attribute.

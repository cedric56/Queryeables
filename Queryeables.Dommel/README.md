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
- **Only columns are mapped**: navigation properties and graph collections are left out without an attribute (Dommel maps
  simple types only); `[NotMapped]` leaves out a simple property too (`NotMappedPropertyResolver`).
  The resolver is global to Dommel: pass `ignoreNotMapped: false` if you have your own, and wrap it.
- **Fast reads**: captured variables are passed as constants (Dommel would compile a lambda for each one), and
  `e => e.Column == value` (a key, a foreign key: what caches and graphs read) runs SQL built once per type and column.
- **Sorted pages and projections**: sorted and projected in memory after the filtered read (EF Core does both in the
  database).
- **SQL logging** (`DbLogConnection`): every command with its parameters, at the level you choose. For development.
- `q.CreateTables()` creates the missing tables of the application's entities at startup, from their classes (SQLite,
  PostgreSQL, SQL Server, MySQL; `DommelSchema.CreateScript` gives the script): for a first application, not a migration
  tool. `DommelEntityStore.EnsureSchemaAsync(factory, connectionString, script)` runs a script of your own. On PostgreSQL,
  a `DateTime` column is a `timestamp with time zone`, as the dates Queryeables writes (`[LastModified]`) are UTC: Npgsql
  then refuses a `DateTime` whose `Kind` is not `Utc` (`DateTime.Now`). Store `DateTime.UtcNow` or a `DateTimeOffset`,
  or declare `[Column(TypeName = "timestamp")]` for a local date and time.

## Table names

A table is named after its class (`Person`), or by `[Table("people")]`. Dommel's own default, the plural (`Persons`,
`Categories`), is still there for a database made that way: `q.PluralizeTableNames()`. Change feeds and PostgreSQL triggers
answer to the class name and to the plural.

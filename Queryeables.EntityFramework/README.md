# Queryeables.EntityFramework

[Queryeables](https://github.com/cedric56/Queryeables) stores on Entity Framework Core, for any EF provider. Targets
**.NET 10**.

```bash
dotnet add package Queryeables.EntityFramework
```

## Set up

```csharp
services.UseQueryeables(q => q
    .UseEntityFramework(sp => new AppDbContext(options))   // one context per store, disposed with it
    .AddGeneratedEntities());
```

## Behavior

`EfEntityStore` behaves like the Dommel store, so that the same code runs on both:

- reads are not tracked (`AsNoTracking`);
- every write is saved immediately, and concerns the entity itself and its owned types only: children and navigation
  properties are written by their own calls (`EntityGraph` does it), never by walking the object graph;
- paging sorts by primary key;
- `[ConcurrencyCheck]` on an `int`/`long` works like with Dommel: a stale entity is a `Conflict`, a vanished one returns
  false;
- transactions go through `Database.BeginTransactionAsync`.

`EfConnectionExtensions` also offers Dommel-shaped CRUD (`SelectAsync`, `InsertAsync`...) directly on an `IDbConnection`,
with one EF provider per connection type (`EfConnectionExtensions.UseProvider<SqliteConnection>((b, c) => b.UseSqlite(c))`).

EF Core builds code at run time: it works with trimming, not with full Native AOT.

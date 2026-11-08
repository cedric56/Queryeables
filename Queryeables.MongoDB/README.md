# Queryeables.MongoDB

[Queryeables](https://github.com/cedric56/Queryeables) on MongoDB, with the official driver. Targets **.NET 10**.

```bash
dotnet add package Queryeables.MongoDB
```

## Set up

```csharp
services.UseQueryeables(q => q
    .UseMongoDB(connectionString, "app")
    .AddGeneratedEntities());
```

`UseMongoDB` registers an `IMongoClient` and an `IMongoDatabase` (unless the application already did) and the stores. The
store is the same `IEntityStore` as on Dommel or EF Core: the same filters, paging, counting, graphs, caches, sync
conventions and remote access.

## Documents

- **Collection:** the `[Table]` name, or the type name. `MongoCollections.NameOf(typeof(Person))` gives it.
- **Key:** the `[Key]` property, or `Id`, stored as `_id`. A key left at its default value is generated on insert:
  - `long` / `int`: from a counter document in the `queryeables_counters` collection (`$inc`, atomic), so that keys stay
    small numbers, as in SQL;
  - `Guid`: `Guid.NewGuid()`; `ObjectId` / `string`: a new `ObjectId`.
- **Concurrency:** a `[ConcurrencyCheck]` version (`long` or `int`) is checked and incremented in the same filter as the
  write. A write over a version that changed fails with `EntityErrorCode.Conflict`; a duplicate key too.
- **Dates:** MongoDB keeps milliseconds. `[LastModified]` values are rounded to the millisecond on write, so that the value
  read back equals the value written.
- **Paging** is sorted by `_id` unless a sort is given; a sort always ends with `_id`, so that pages do not overlap.
- The mapping is the driver's (conventions, `[BsonElement]`, class maps): Queryeables adds nothing on top.

## Transactions

`BeginTransactionAsync` opens a session with a transaction; the reads and writes of the store go through it until commit
or rollback. MongoDB only has transactions on a **replica set** (a single node is enough) or a sharded cluster:

```bash
docker run -d -p 27017:27017 mongo:8.0 --replSet rs0 --bind_ip_all
docker exec <container> mongosh --eval "rs.initiate({ _id: 'rs0', members: [{ _id: 0, host: 'localhost:27017' }] })"
# connection string: mongodb://localhost:27017/?replicaSet=rs0&directConnection=true
```

On Linux 6.19 and newer, MongoDB 8.x refuses to start ("Linux kernel versions 6.19 and newer has a known
incompatibility", [SERVER-121912](https://jira.mongodb.org/browse/SERVER-121912)): use `mongo:7.0` there, as the CI does.

## Change notifications

A change stream reports every insert, update, replace and delete of the watched collections to the change feeds, whoever
made it (the application, another process, the shell); the caches follow them.

```csharp
services.UseQueryeables(q => q
    .UseMongoDB(connectionString, "app", notifications: n => n
        .Watch<Person>()
        .Watch<Contact>())
    .AddGeneratedEntities());
```

- `MongoChangeListener`, a hosted service, opens one change stream on the database, filtered on the watched collections.
  Without a host (console application), call `provider.GetRequiredService<MongoChangeListener>().StartAsync()`;
  `Listening` completes once the stream is open.
- After a disconnection, the stream resumes from its last resume token. When it cannot (the oplog moved on), or when a
  collection is dropped or renamed, the caches synchronize instead.
- Change streams need a replica set, as transactions do.

## Native AOT

The MongoDB driver maps documents by reflection and generates code at run time: `UseMongoDB` and the store are marked
`[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`, like EF Core. A Native AOT client reaches MongoDB through a
Queryeables server (`UseMagicOnion`).

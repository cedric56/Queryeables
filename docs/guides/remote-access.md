# Remote access

A client uses the same services against a server: `IEntityStore` becomes a `RemoteEntityStore`, and the server routes each
call to its own stores.

## The layers

| Layer | Client | Server |
| --- | --- | --- |
| Your code | `IPersonRepository`, `IEntityCache<Person, long>` | (the same entities) |
| Remote core (`Queryeables`) | `RemoteEntityStore` | `EntityRequestDispatcher`, `EntityHandler<T>` |
| Transport | `IEntityTransport` | forwards to the dispatcher |
| MagicOnion | `Queryeables.MagicOnion.Client` | `Queryeables.MagicOnion.Server` |

The remote core does not depend on MagicOnion: a transport implements `IEntityTransport` on the client and calls the
dispatcher on the server. `dispatcher.AsTransport()` connects both in process, which the sample and the tests use.

## Server

```csharp
public sealed class AppLayerService(EntityRequestDispatcher dispatcher) : MagicOnionService(dispatcher);

builder.Services.AddMagicOnion();
builder.Services.UseQueryeables(q => q
    .UseSqlite(builder.Configuration.GetConnectionString("App")!)
    .AddGeneratedEntities()
    .AddMagicOnionServer(server => server
        .Expose<Person>(EntityAccessPolicies.Authenticated)
        .Expose<Contact>(EntityAccessPolicies.Authenticated)
        .Expose<Product>(EntityAccessPolicies.ReadOnly)));

app.MapMagicOnionService();
```

For each call, the server checks the policy of the type, validates the entity (DataAnnotations) for inserts and updates,
then uses its stores. A type with a graph repository is saved with its children.

## Client

```csharp
services.UseQueryeables(q => q
    .UseMagicOnion("https://server:5001")
    .AddGeneratedEntities());
```

- **Predicates** become conditions, with `&&`, `||`, parentheses and `!`; an unsupported one throws
  `NotSupportedException` before any call.
- **Transactions**: writes are queued and sent in one batch on commit, applied in one server transaction, parents with their children included. Until then,
  `UpdateAsync` and `DeleteAsync` return true, an inserted entity has no key yet, and reads do not see the pending writes.
- **Results**: after a write, the keys, versions and dates set by the server are copied into your objects; navigation
  properties and members the serializer ignores are kept.
- **Errors**: reads and writes throw `EntityStoreException` with the server's error kind; transactions return a `EntityResult`.

## Entities on the wire

MagicOnion uses MessagePack: `[MessagePackObject(keyAsPropertyName: true)]` on each entity (the template's choice: every
public property is carried under its name, inherited ones included), or `[MessagePackObject]` with `[Key(n)]` on each
carried property (smaller messages; never reuse a number). `[IgnoreMember]` on navigation properties, and on what must
never leave the server: MessagePack ignores `[JsonIgnore]` (see [security](security.md#a-value-that-never-leaves-the-server)).
Keep the entities in a library referenced by the client and the server: `AddGeneratedEntities()` registers them on both sides.

## Limits

- Writes need the server, unless the [offline mode](offline-mode.md) queues them.
- At most 1000 conditions per filter and 1000 values per `Contains`.
- Reads and writes are authorized per type by a policy, and per row by a row filter (see [security](security.md#row-filters)).
- Text search (`Contains`, `StartsWith`, `EndsWith`) is case-sensitive on SQLite, PostgreSQL and MongoDB; on SQL Server and
  MySQL, it follows the column's collation (case-insensitive by default).

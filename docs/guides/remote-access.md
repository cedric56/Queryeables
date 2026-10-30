# Remote access

A client uses the same services against a server: `IEntityStore` becomes a `RemoteEntityStore`, and the server routes each
call to its own stores.

## The layers

| Layer | Client | Server |
| --- | --- | --- |
| Your code | `IPersonService`, `IEntityCache<Person, long>` | (the same entities) |
| Remote core (`Queryeables`) | `RemoteEntityStore` | `EntityRequestDispatcher`, `EntityHandler<T>` |
| Transport | `IEntityRemote` | forwards to the dispatcher |
| MagicOnion | `Queryeables.Onion.Client` | `Queryeables.Server` |

The remote core does not depend on MagicOnion: a transport implements `IEntityRemote` on the client and calls the
dispatcher on the server. `dispatcher.AsRemote()` connects both in process, which the sample and the tests use.

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
then uses its stores. A type with a graph service is saved with its children.

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
- **Errors**: reads and writes throw `EntityStoreException` with the server's error kind; transactions return a `DbResult`.

## Entities on the wire

MagicOnion uses MessagePack: `[MessagePackObject]` on each entity, `[Key(n)]` on each carried property (inherited ones
included), `[IgnoreMember]` on navigation properties. Keep the entities in a library referenced by the client and the
server: `AddGeneratedEntities()` registers them on both sides.

## Limits

- No offline mode: writes need the server.
- At most 1000 conditions per filter and 1000 values per `Contains`.
- Reads are authorized per type, not per row: expose a type only to users who may read all of it, or write a dedicated
  server method.

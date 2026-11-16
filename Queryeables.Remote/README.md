# Queryeables.Remote

The replica core of [Queryeables](https://github.com/cedric56/Queryeables), independent of any transport: a server
publishes its entity types, each client keeps a read-only replica of the rows its user may read. This project is not
published on its own: it ships inside the [`Queryeables`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.Core/README.md) package.

## Server

`EntityRequestDispatcher` receives the calls and routes them to the exposed types (`EntityHandler<T>`), each with its access
policy (`IEntityAccessPolicy`) and row filter (`RowLevelSecurity<T>`):

```csharp
q.AddEntityServer(serializer, server => server
    .Expose<Order>(EntityAccessPolicies.Authenticated,
        rows: RowLevelSecurity.For<Order, long>(o => o.Id, user => o => o.OwnerId == OwnerOf(user)))
    .Expose<Product>(EntityAccessPolicies.AllowAll));
```

A handler serves three reads: every row the user may read, the rows changed since a date (types with `[LastModified]`),
and a row by its key. The row filter applies to all three. A read of more than `maxReadSize` rows (10,000 by default) is
refused, not cut. A policy decides per type and per user (`EntityAccessRequest(EntityType, User)`):
`EntityAccessPolicies.Authenticated`, `AllowAll`, `From(rule)`, `FromAsync(rule)`.

## Client

`RemoteEntityStore` is the `IEntityStore` of the caches of a client, their replicas. It talks to an `IEntityTransport`,
with the entities as bytes written by an `IEntitySerializer` (`JsonEntitySerializer` by default):

```csharp
var stores = RemoteEntityStore.Factory(transport, serializer);   // IsRemote = true
```

It reads every row (`GetAllAsync`), the changes (`SelectChangesAsync`) and a row by key (`e => e.Id == id`). Any other
read, and every write, throws `EntityStoreException` (`NotSupported`) before any call: a client queries its replica in
memory, and writes through a service of the application.

`dispatcher.AsTransport(context)` connects both sides in process: for tests, or an application that hosts its own server.

## Writing a transport

Implement the three methods of `IEntityTransport` on the client (`GetAllAsync`, `SelectAsync` with the key condition,
`GetChangesAsync`), and forward each call to `EntityRequestDispatcher` on the server with an `EntityRequestContext` (the
authenticated user). `TransportResult` carries the data or the error kind; `RemoteDiagnostics` times and traces the calls.
[`Queryeables.MagicOnion`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.MagicOnion/README.md) is the
MagicOnion transport.

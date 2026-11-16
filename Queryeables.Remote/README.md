# Queryeables.Remote

Remote access for [Queryeables](https://github.com/cedric56/Queryeables), independent of any transport: a server publishes its
entity types, each client keeps a replica of the rows its user may read. This project is not published on its own: it ships
inside the [`Queryeables`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.Core/README.md) package.

## Client

`RemoteEntityStore` is an `IEntityStore` that talks to an **`IEntityTransport`** (entities as bytes, written by an
**`IEntitySerializer`**), for the caches of the client, its replicas:

```csharp
var stores = RemoteEntityStore.Factory(remote, serializer);   // IsRemote = true
```

- Every row the user may read (`GetAllAsync`), the rows changed since a date (`SelectChangesAsync`), and a row by its key
  (`e => e.Id == id`): what a cache loads, then reads again when the server notifies a change.
- Anything else, and every write, throws `EntityStoreException` (`NotSupported`) before any call: a client queries its
  replica in memory and writes through a service of the application.

## Server

`EntityRequestDispatcher` receives the calls and routes them to the exposed types (`EntityHandler<T>`), each with its access
policy (`IEntityAccessPolicy`) and row filter (`RowLevelSecurity<T>`). A replica holds at most `maxReadSize` rows: beyond, the
read is refused, not cut.

```csharp
q.AddEntityServer(serializer, server => server
    .Expose<Person>(EntityAccessPolicies.Authenticated)
    .Expose<Product>(EntityAccessPolicies.AllowAll));
```

`dispatcher.AsTransport(context)` connects both sides in-process: for tests, or an application that hosts its own server.

## Extension points

What a server serves is the handler of each type: `EntityServerBuilder.Handlers` (an `IEntityHandlerFactory`) replaces the
read-only `EntityHandler<T>`. On the client, `QueryeablesBuilder.DecorateStores` wraps the stores.

## Writing a transport

Implement `IEntityTransport` on the client, and forward each call to `EntityRequestDispatcher` on the server with an
`EntityRequestContext` (the authenticated user, the notification connection). `TransportResult` carries the data or the
error kind; `RemoteDiagnostics` times and traces the calls. [`Queryeables.MagicOnion`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.MagicOnion/README.md)
is the MagicOnion transport.

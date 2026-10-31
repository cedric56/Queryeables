# Queryeables.Remote

Remote access for [Queryeables](https://github.com/cedric56/Queryeables), independent of any transport. This project is not
published on its own: it ships inside the [`Queryeables`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.Core/README.md) package.

## Client

`RemoteEntityStore` is an `IEntityStore` that talks to an **`IEntityTransport`**: one method per operation, entities as bytes,
written by an **`IEntitySerializer`**.

```csharp
var stores = RemoteEntityStore.Factory(remote, serializer, session);   // IsRemote = true
```

- Predicates are sent as conditions: `==`, `!=`, `<`, `<=`, `>`, `>=`, booleans, `ids.Contains(e.Id)`, and `StartsWith`,
  `EndsWith`, `Contains` on strings, combined with `&&`, `||`, parentheses and `!` (pushed down to the terms). Anything
  else throws `NotSupportedException` before any call.
- Sorted pages (`SelectPagedAsync(predicate, page, size, Sort<T>.By(...))`) are sorted by the server: only the page
  crosses the network. The sort names properties, checked by the server against the exposed type. Projections run on
  the client.
- In a transaction, writes are queued and sent in one batch on commit: all applied, or none. A parent with an entity
  graph is saved with its children inside that batch.
- After a write, the keys, versions and dates set by the server are copied into your objects. Navigation properties and
  members the serializer does not carry are left as they are.

## Offline mode

`OfflineMode` keeps a local copy of chosen types and a queue of the writes made without the server, and tells the
application when the server is lost or back and when the data differs; the user chooses when to switch.
`OfflineStoreFactory` gives remote stores online and stores on the copy offline. Storage: `BrowserOfflineStorage`
(WebAssembly), `FileOfflineStorage`, `MemoryOfflineStorage`, or your own `IOfflineStorage`. Register it with
`q.WithOfflineMode(...)`: see the [offline mode guide](https://github.com/cedric56/Queryeables/blob/master/docs/guides/offline-mode.md).

## Server

`EntityRequestDispatcher` receives the same calls and routes them to the exposed types (`EntityHandler<T>`), each with its
access policy (`IEntityAccessPolicy`) and DataAnnotations validation. A type with an `IAggregateRepository<T>` is saved with its
children. Filters are rebuilt from a whitelist of operators; no SQL and no expression comes from the client.

```csharp
q.AddEntityServer(serializer, server => server
    .Expose<Person>(EntityAccessPolicies.Authenticated)
    .Expose<Product>(EntityAccessPolicies.ReadOnly));
```

`dispatcher.AsTransport(context)` connects both sides in-process: for tests, or an application that hosts its own server.

## Writing a transport

Implement `IEntityTransport` on the client, and forward each call to `EntityRequestDispatcher` on the server with an
`EntityRequestContext` (the authenticated user, the notification connection). `TransportResult` carries the data or the
error kind. [`Queryeables.Onion`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.Onion/README.md) is the MagicOnion transport.

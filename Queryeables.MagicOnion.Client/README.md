# Queryeables.MagicOnion.Client

Use a Queryeables server from a client application (desktop, mobile, browser) over
[MagicOnion](https://github.com/Cysharp/MagicOnion). The client code is the same as with a local database: only the
provider changes. Targets **.NET 10**.

```bash
dotnet add package Queryeables.MagicOnion.Client
```

## Set up

```csharp
services.UseQueryeables(q => q
    .UseMagicOnion("https://server:5001")      // instead of UseSqlite(...)
    .AddGeneratedEntities());
```

`IEntityStoreFactory` then creates `RemoteEntityStore` instances (`IsRemote` is true): the caches of the client are replicas
of what the server publishes (the rows its user may read), kept up to date by its notifications and queried in memory.
Any other query, and every write, throws `NotSupported`. The client writes through the services of the application:

```csharp
var people = app.Create<IPeopleService>("https://server:5001");   // a MagicOnion service of the server
await people.AddAsync("Ana", ["friend"]);                         // the replicas follow by the notification
```

The channel goes over HTTP/2 (`SocketsHttpHandler`), and through the gRPC WebSocket bridge in the browser (the server
must enable the bridge). The server certificate is validated by the system on every platform, Android included. Pass
`handler` to use your own `HttpMessageHandler`, and `validateCertificate` to accept a known self-signed certificate: it
replaces the system's decision, on every platform (ignored in the browser), and a handler that cannot use it is an error
rather than a silent skip. An `https://` URL needs a server listening in HTTPS on that port.

## Change notifications

```csharp
var monitor = provider.GetRequiredService<IChangeFeed<Person>>();
var unsubscribe = await monitor.AddStreamingHub("https://server:5001", provider.GetRequiredService<IChangeSession>(), logger: logger);
...
await unsubscribe();
```

The server's changes reach the change feed, hence the caches, this client's own writes included. When the server is
unreachable, the error is logged and nothing is subscribed: call it again later. The hub does not reconnect by itself:
after a reconnection, call `cache.SyncAsync()` to catch up.

`MagicOnionClientExtensions.CreateChannel(url)` creates a channel for your own MagicOnion clients.

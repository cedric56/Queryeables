# Queryeables.SignalR

Change notifications for [Queryeables](https://github.com/cedric56/Queryeables) clients over ASP.NET Core **SignalR**
(MessagePack protocol), as an alternative to the MagicOnion hub. Targets **.NET 10**.

```bash
dotnet add package Queryeables.SignalR
```

## Use

```csharp
var monitor = provider.GetRequiredService<IChangeFeed<Person>>();
var unsubscribe = await monitor.AddSignalR("https://server:5001/hubs/person", provider.GetRequiredService<IChangeSession>(),
    logger: logger);
...
await unsubscribe();
```

The server's changes are published on the change feed, hence the caches; this client's own changes are not echoed back.
Unlike the MagicOnion hub, the connection retries and reconnects by itself (`FiveSecondsReconnectPolicy` by default, or
your own `IRetryPolicy`). After a reconnection, call `cache.SyncAsync()` to catch up with the changes missed meanwhile.
The server certificate is validated by the system on every platform. `validateCertificate` replaces that decision (a
known self-signed certificate, by its hash) for the HTTP requests and the WebSocket alike; it is ignored in the browser.

The server side is [`Queryeables.SignalR.Server`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.SignalR.Server/README.md).

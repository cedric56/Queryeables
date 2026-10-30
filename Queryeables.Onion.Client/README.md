# Queryeables.Onion.Client

Use a Queryeables server from a client application (desktop, mobile, browser) over
[MagicOnion](https://github.com/Cysharp/MagicOnion). The client code is the same as with a local database: only the
provider changes. Targets **.NET 10**.

```bash
dotnet add package Queryeables.Onion.Client
```

## Set up

```csharp
services.UseQueryeables(q => q
    .UseMagicOnion("https://server:5001")      // instead of UseSqlite(...)
    .AddGeneratedEntities());
```

`IEntityStoreFactory` then creates `RemoteEntityStore` instances (`IsRemote` is true): the generated services send a parent
with its children in one call, and the server saves the children with its own graph service.

The channel goes through the gRPC WebSocket bridge by default, which works everywhere, the browser included; the server must
enable the bridge. Pass `handler` to use your own `HttpMessageHandler`, and `validateCertificate` to accept a known
self-signed certificate (ignored in the browser). An `https://` URL needs a server listening in HTTPS on that port.

## Change notifications

```csharp
var monitor = provider.GetRequiredService<IDbMonitor<Person>>();
var unsubscribe = await monitor.AddStreamingHub("https://server:5001", provider.GetRequiredService<IDbSession>(), logger: logger);
...
await unsubscribe();
```

The server's changes reach the monitor, hence the caches; this client's own changes are not echoed back. When the server is
unreachable, the error is logged and nothing is subscribed: call it again later. The hub does not reconnect by itself:
after a reconnection, call `cache.SyncAsync()` to catch up.

`MagicOnionClientExtensions.CreateChannel(url)` and `provider.Create<TService>(url)` create channels and clients for your
own MagicOnion services.

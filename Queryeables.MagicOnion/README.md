# Queryeables.MagicOnion

The [MagicOnion](https://github.com/Cysharp/MagicOnion) transport of [Queryeables](https://github.com/cedric56/Queryeables):
what the client and the server share. Targets **.NET 10**.

You do not reference it directly: [`Queryeables.MagicOnion.Client`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.MagicOnion.Client/README.md) and
[`Queryeables.MagicOnion.Server`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.MagicOnion.Server/README.md) bring it.

## Contents

| Type | Role |
| --- | --- |
| `IMagicOnionLayer` | The MagicOnion service: the three reads of a replica (every row, a row by key, the changes) |
| `EntityResultMessage`, `FilterConditionMessage` | Its MessagePack messages |
| `MagicOnionTransport` | `IEntityTransport` on top of `IMagicOnionLayer`: plugs MagicOnion into `RemoteEntityStore` |
| `MessagePackEntitySerializer` | `IEntitySerializer` with MessagePack (`Default` on the client, `Server` on the server) |
| `IChangeHub<T>`, `IChangeHubReceiver<T>`, `ChangeMessage` | The change notification hub |
| `ChangeHubReceiver<T>` | Client side of the hub: publishes the server's changes on the local change feed |

## Entities

Entities travel as JSON (`JsonEntitySerializer`, the default of `UseMagicOnion` and `AddMagicOnionServer`): plain classes,
a navigation back to the parent written once. To keep MessagePack, pass `MessagePackEntitySerializer.Default` (client) and
`MessagePackEntitySerializer.Server` (server), and mark the entities `[MessagePackObject]` with `[IgnoreMember]` on the
navigations. Under Native AOT, give a `JsonEntitySerializer` on a `JsonSerializerContext`, or use the MessagePack source
generator.

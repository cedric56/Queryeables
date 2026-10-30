# Queryeables.Onion

The [MagicOnion](https://github.com/Cysharp/MagicOnion) transport of [Queryeables](https://github.com/cedric56/Queryeables):
what the client and the server share. Targets **.NET 10**.

You do not reference it directly: [`Queryeables.Onion.Client`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.Onion.Client/README.md) and
[`Queryeables.Server`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.Server/README.md) bring it.

## Contents

| Type | Role |
| --- | --- |
| `IMagicOnionLayer` | The MagicOnion service: one method per store operation |
| `DBResultMessage`, `FilterConditionMessage`, `BatchOperationMessage` | Its MessagePack messages |
| `MagicOnionRemote` | `IEntityRemote` on top of `IMagicOnionLayer`: plugs MagicOnion into `RemoteEntityStore` |
| `MessagePackEntitySerializer` | `IEntitySerializer` with MessagePack (`MessagePackEntitySerializer.Default`) |
| `IRecordHub<T>`, `IRecordHubReceiver<T>`, `RecordMessage` | The change notification hub |
| `RecordHubReceiver<T>` | Client side of the hub: publishes the server's changes on the local monitor |

## Entities

Entities travel as MessagePack: mark them `[MessagePackObject]` with a `[Key(n)]` on every persisted property, including
the key and inherited ones, and `[IgnoreMember]` on navigation properties (a child pointing back to its parent would loop).
Graph collections are carried (`[Key(n)]` on `Contacts`): the server saves them with the parent. Under Native AOT, use the
MessagePack and MagicOnion source generators.

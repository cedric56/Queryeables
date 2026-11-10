# Queryeables.Replication

A durable server for the [Routeables Remote Protocol](https://github.com/cedric56/Routeables.SignalR/blob/master/PROTOCOL-REMOTE.md)
(RRP): clients write offline, the server process stops and starts again, and nothing is lost or applied twice, because the
log, the merged documents and the replicas live in your database, through any Queryeables store (EF Core, MongoDB, Dommel,
PostgreSQL).

```csharp
var store = new EntityReplicationStore(stores,              // IEntityStoreFactory: EF Core, MongoDB, Dommel...
[
    // The documents of "Person", written to your own entities in the same transaction: the read model of the server.
    new ReplicationProjection<Person>("Person",
        key => p => p.Key == key,
        (document, existing) =>
        {
            var person = existing ?? new Person { Key = document.Id.Key };
            person.Name = document.Get("name")?.Trim('"');
            person.Visits = document.Counter("visits");
            return person;
        }),
]);

builder.Services.AddRouteablesReplication(_ => new ReplicationServer(policy, store: store));   // Routeables.SignalR
app.MapRouteablesReplication().RequireAuthorization();
```

## Tables

Four entities, mapped like any other of the store:

| Entity | One row per | What a server reads it for |
| --- | --- | --- |
| `ReplicationLogRow` | change admitted (`Position`, `Collections` as `\|Person\|Note\|`, `Change` as JSON) | a pull: the entries after a cursor |
| `ReplicationDocumentRow` | document (`Collection`, `Key`, `State` as JSON) | the rules, the merge, a snapshot |
| `ReplicationReplicaRow` | replica (`Replica`, `User`, `LastSeq`, `Acks`) | a push sent again after a lost answer |
| `ReplicationHeadRow` | — (one row: `Position`, `Horizon`) | the next position, the compaction horizon |

With EF Core, map them in your `DbContext` (unique indexes on `Position`, `(Collection, Key)` and `Replica`); with MongoDB,
they are collections; with Dommel, create the tables.

## Guarantees

- **One transaction per decision**: the log entry, the documents it changed, the replica (its seq and answer), the head and
  the projections. A crash never leaves a change half applied.
- **One writer.** `ReplicationServer` decides one change at a time; two server processes over one database would both
  write the next position: the second one fails (`another server wrote to this log`) instead of corrupting it. A lock in the
  database is needed for several writers; version 1 does not provide it.
- **Transactions required**: EF Core, Dommel, PostgreSQL; MongoDB as a replica set.
- `CompactAsync(position)` removes the old entries; clients behind them get a snapshot from the documents.

The tests (`ReplicationStoreContractTests`) run the same scenarios on the in-memory store, EF Core on SQLite and MongoDB:
two devices write offline, an answer is lost, the server restarts over the same database, everything converges, the
projected table matches.

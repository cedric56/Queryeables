# Queryeables.Abstraction

The contracts of [Queryeables](https://github.com/cedric56/Queryeables), with no package dependency. This project is not
published on its own: it ships inside the [`Queryeables`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.Core/README.md) package.

| Area | Types |
| --- | --- |
| Stores | `IEntityStore`, `IEntityTransaction`, `IEntityStoreFactory`, `EntityStoreFactory`, `EntityStoreExtensions`, `Paging` |
| Results and errors | `EntityResult`, `EntityResult<T>`, `EntityErrorCode`, `EntityStoreException`, `EntityValidator` |
| Graphs | `EntityGraph`, `EntityGraph<TParent, TKey>`, `GraphRepository<TParent, TKey>`, `IAggregateRepository<T>`, `[EntityGraph]` |
| Caches | `IEntityCache<T, TKey>`, `EntityCache<T, TKey>`, `[EntityCache]` |
| Synchronization | `[LastModified]`, `[SoftDelete]`, `SyncConventionsDecorator`, `SyncMetadata`, `ConcurrencyToken` |
| Change notifications | `IChangeFeed<T>`, `ChangeFeed<T>`, `ChangeFeedRegistry`, `EntityChange<T>`, `EntityKey`, `ChangeContext`, `IChangeSession` |
| Transports | `ChangeBroadcaster<T>` (server), `ChangeRelay<T>` (client): shared by MagicOnion and SignalR |
| Helpers | `CollectionExtensions.Update`: aligns a collection with a selection, in place |

Rule of this project: no `PackageReference`. Everything here builds on the base class library only, and is trimming
and AOT compatible.

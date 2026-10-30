# Credits and sources of inspiration

Queryeables borrows **design ideas** from other open-source projects. No code was copied:
each feature was rewritten to fit the Queryeables model (`IEntityStore`, plain entities,
Dommel, EF Core, MagicOnion). Licenses are listed for information.

| Project | License | What was borrowed (idea) | Where in Queryeables |
|---|---|---|---|
| [Datasync Community Toolkit](https://github.com/CommunityToolkit/Datasync) (.NET Foundation, formerly Azure Mobile Apps) | MIT | Modification date (`UpdatedAt`) and soft delete (`Deleted`) for incremental synchronization; "since the last row received" token; maximum page size enforced by the server (`PageSize`, `MaxTop`); optimistic concurrency through a version | `[LastModified]`, `[SoftDelete]`, `SyncConventionsStore`, `IEntityStore.SelectChangesAsync`, `EntityCache.SyncAsync`, `maxPageSize` parameter of `EntityHandler`, `ConcurrencyToken` |
| [Azure Mobile Apps — offline sync](https://azure.microsoft.com/en-gb/documentation/articles/app-service-mobile-offline-data-sync/) | Microsoft documentation | Incremental token principle: remember the date of the last row received; the next synchronization only asks for what changed since | `EntityCache.SyncAsync` |
| [ActualLab.Fusion](https://github.com/ActualLab/Fusion/wiki) | MIT | Invalidation coalescing: a burst of changes triggers a single recomputation | Notification coalescing in `EntityCache` |
| [ABP Framework — repositories](https://abp.io/docs/10.1/framework/architecture/domain-driven-design/repositories) | LGPL-3.0 | API ideas only: `GetAsync` that throws when nothing matches (next to `FindAsync`/`FirstOrDefaultAsync`), bulk operations `InsertMany`/`UpdateMany`/`DeleteMany` | `EntityStoreExtensions.GetAsync`, `InsertManyAsync`, `UpdateManyAsync`, `DeleteManyAsync` |
| [Breeze](https://www.nuget.org/packages/Breeze.Persistence/7.3.1) | MIT | Saving all of a client's changes in a single transaction | Remote batches (`ExecuteBatchAsync`, `RemoteEntityStore` transactions) |

# Introduction

Queryeables is a data-access layer for .NET applications whose data lives **either in a local database or behind a server**,
and that want the same code for both. It sits on top of an ORM (Dommel or EF Core), not instead of it.

## The model

| Piece | What it is |
| --- | --- |
| `IEntityStore` | One unit of work: CRUD, filters, paging, counting, transactions. Implemented on Dommel, EF Core and a remote server |
| `IEntityStoreFactory` | Creates stores; what the provider registers |
| `DbResult` | The outcome of a transaction: success, or an error kind (`Validation`, `Conflict`, `NotFound`, `Forbidden`...) |
| `[EntityGraph]` | A parent saved, removed and loaded with its children, in one transaction |
| `[EntityCache]` | An in-memory copy of a table, kept up to date by change notifications |
| `IDbMonitor<T>` | The change notifications of a table: from SQLite, SQL Server, or a server's hub |
| Source generator | Writes the graph services and the registration; reports attribute mistakes at compile time |

Switching from local to remote changes the provider, not the code that uses the data:

```csharp
q.UseSqlite("Data Source=app.db")       // a desktop application with its own database
q.UseMagicOnion("https://server:5001")  // the same application, against a shared server
```

## When to choose Queryeables

- A desktop or mobile application that starts with a local database and may later share its data through a server.
- Several clients that must see each other's changes (caches that follow the database, notifications that skip their author).
- Parent-child aggregates (an order and its lines, a person and its contacts) saved as one unit, locally or remotely.
- A server that exposes entities to clients without writing one endpoint per entity, with an access policy per type.

## When to choose something else

Queryeables is young and has a precise scope. Other tools are better in these cases:

| Need | Better choice | Why |
| --- | --- | --- |
| Offline-first mobile app: write while disconnected, push later | [Datasync Community Toolkit](https://github.com/CommunityToolkit/Datasync) | It keeps an offline queue and resolves conflicts on sync. Queryeables' remote store needs the server to write; its caches only catch up after a reconnection |
| Strong real-time consistency, computed values that invalidate themselves | [ActualLab.Fusion](https://github.com/ActualLab/Fusion) | Its dependency tracking and RPC go much further than table-level notifications |
| A complete application framework: modules, multi-tenancy, UI, identity | [ABP](https://abp.io) | Queryeables is one brick, not a platform |
| A second-level cache for an existing EF Core application | [EFCoreSecondLevelCacheInterceptor](https://github.com/VahidN/EFCoreSecondLevelCacheInterceptor) | Transparent query caching, no change to your code |
| Complex queries: joins, projections, grouping, raw SQL | EF Core or Dapper directly | `IEntityStore` filters one entity type with simple conditions; use the ORM for the rest, next to Queryeables |

Queryeables does not hide the ORM: a Dommel or EF Core application can use its connection or `DbContext` for complex
queries, and Queryeables for the entities that need caches, graphs or remote access.

## What it borrows

The design ideas come from the Datasync Community Toolkit (incremental synchronization, soft delete), ActualLab.Fusion
(invalidation coalescing), ABP (repository API) and Breeze (saving a client's changes in one transaction). See
[CREDITS.md](https://github.com/cedric56/Queryeables/blob/master/CREDITS.md).

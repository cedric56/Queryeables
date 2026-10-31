# Changelog

All notable changes are listed here. Versions follow [Semantic Versioning](https://semver.org); until 1.0, a minor version
may change the API.

## [Unreleased]

First public version.

### Changed
- Names aligned on known concepts, one vocabulary per idea:

  | Before | After |
  | --- | --- |
  | `DbRecord<T>`, `DbRecordKey` | `EntityChange<T>`, `EntityKey` |
  | `IDbMonitor<T>`, `DbMonitor<T>`, `DbMonitorRegistry`, `AddMonitor<T>()`, `RecordChanged` | `IChangeFeed<T>`, `ChangeFeed<T>`, `ChangeFeedRegistry`, `AddChangeFeed<T>()`, `ChangeReceived` |
  | `RecordHub`, `RecordHubReceiver`, `RecordHubRepository`, `RecordMessage`, `SignalRRecord` | `ChangeHub`, `ChangeHubReceiver`, `ChangeHubConnections`, `ChangeMessage`, `SignalRChange` |
  | `IDbSession`, `DbSession` | `IChangeSession`, `ChangeSession` |
  | `IDbTransaction<T>`, `IEntityGraphService<T>`, `EntityGraphService<,>`, generated `I{Parent}Service` | `IAggregateRepository<T>`, `IGraphRepository<T>`, `GraphRepository<,>`, generated `I{Parent}Repository` |
  | `DbResult`, `DbResult<T>`, `RemoteResult` | `EntityResult`, `EntityResult<T>`, `TransportResult` |
  | `IEntityRemote`, `MagicOnionRemote`, `AsRemote()` | `IEntityTransport`, `MagicOnionTransport`, `AsTransport()` |
  | `EntityRowFilter` | `RowLevelSecurity` |
  | `SyncConventionsStore` | `SyncConventionsDecorator` |

### Added
- `IEntityStore` on Dommel, EF Core and a remote server, with transactions that return a `EntityResult`.
- Providers: SQLite (with change notifications), PostgreSQL, any Dommel database, EF Core, MagicOnion client.
- `[EntityGraph]`: parents saved, removed and loaded with their children; `[EntityCache]`: caches that follow the database.
- Source generator: graph repositories, registration (`AddGeneratedEntities()`, including referenced projects), QRY001-QRY009.
- Remote access independent of the transport; MagicOnion transport; change notifications over MagicOnion and SignalR.
- `[LastModified]`, `[SoftDelete]`, `[ConcurrencyCheck]`; incremental synchronization of caches.
- Documentation site, benchmarks, in-process remote sample.
- Remote filters with any nesting of `&&`, `||`, parentheses and `!` (sent in postfix order; flat filters keep the flat form).
- Parents with an entity graph inside remote batches (`InsertManyAsync`, transactions): saved with their children.
- `IEntityGraph<TParent>`, to run a graph in a transaction opened elsewhere.
- Native AOT smoke test, published and run by the CI.
- Concurrency tests: parallel notifications, broadcast order, reconnection, stuck clients.

- `CapturedValues`: predicates reach Dommel and the remote filters with constants instead of captured variables, which
  Dommel used to evaluate by compiling a lambda per query.
- PostgreSQL change notifications: triggers and `LISTEN/NOTIFY`, a hosted listener that reconnects and makes the caches
  synchronize (`UseNpgsql(..., notifications: n => n.Watch<T>(...))`), tested against a real server in the CI.
- `IChangeFeed.PublishResync()`: tells subscribers that changes may have been missed.
- Row filters (`RowLevelSecurity.For<T, TKey>(key, user => predicate)`, `Expose<T>(policy, rows: ...)`): each user reads and
  writes its own rows only; someone else's row answers NotFound.
- `maxReadSize` (10,000 by default): reads that are not paged (every row, a filter, the changes) fail beyond it instead of
  sending the table.
- `SyncOverlap` (5 seconds) on caches and the offline mode: incremental synchronization asks again over a window before the
  latest date received, for late commits and clocks that differ.
- Offline mode (`WithOfflineMode`, `OfflineMode`): a local copy and a queue of writes when the server cannot be reached,
  temporary keys, coalesced writes, incremental downloads ([LastModified] and [SoftDelete]), a scope per type (`where`),
  storage row by row, graphs and transactions offline, conflicts resolved by the user, storage in the
  browser (`BrowserOfflineStorage`), in files or in memory. The user is told when the server is lost or back and when
  the data differs; the mode never switches by itself.
- `Query<T>()`: `Where`, `OrderBy`/`ThenBy`, `Skip`, `Take`, `Page`, `Select`, then `ToListAsync`, `FirstOrDefaultAsync`,
  `FirstAsync`, `CountAsync`, `AnyAsync`, on a store or a factory.
- Sorting: `Sort<T>.By(...).ThenByDescending(...)`, `SelectPagedAsync(predicate, page, size, sort)` (in the database with
  EF Core, by the server remotely) and `SelectAsync(predicate, sort)`.
- Projections: `SelectAsync<T, TResult>(predicate, selector)`, in the database with EF Core.
- Dommel store: `e => e.Column == value` (reads by key and by foreign key) runs SQL built once per type and column.

### Fixed
- EF Core store: `EfEntityStore`, `EfConnectionExtensions` and `UseEntityFramework` carry `RequiresUnreferencedCode` and
  `RequiresDynamicCode`, like EF Core itself: the build has no trimming warnings, and a trimmed or Native AOT application
  is warned where it uses them.
- SQLite: changes are reported once the commit has returned, not from inside the commit hook, so that a cache reading the
  row again never sees its previous state.

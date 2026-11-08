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
- Convergence tests under load: several clients writing online and offline, networks cut, answers lost, conflicts
  decided at random; at the end every client agrees with the server and no write was applied twice. Seeded and
  replayable; `QUERYEABLES_LOAD_SCALE` for longer runs.
- `NetworkBenchmarks`: the same calls on the database, in process, and through gRPC to a real server on 127.0.0.1. The CI
  runs them once, which also checks the whole MagicOnion chain end to end.
- `EncryptedOfflineStorage`: the offline copy encrypted at rest (AES-256-GCM, names hashed with HMAC-SHA256), around any
  `IOfflineStorage`. Not in Blazor WebAssembly, which has no AES-GCM.
- `EntityCache.Freshness` (`NotLoaded`, `Current`, `Stale`), `LastRefreshedAt` and `LastError`: how far to trust a cache
  whose last update failed.
- `IEntityTransport.WriteOnceAsync`, `EntityRequestDispatcher.WriteOnceAsync` and `IIdempotencyStore`
  (`MemoryIdempotencyStore` by default): a write sent again with the same operation id is applied once. MagicOnion
  carries it (`IMagicOnionLayer.WriteOnceAsync`: client and server must be updated together).
- `IEntityStore.SelectChangesAsync(since, predicate, maxRows)`: changes filtered and bounded by the database.
- CI: tests on Windows and macOS; actions pinned by commit, kept up to date by Dependabot.
- MongoDB provider, `Queryeables.MongoDB`: `q.UseMongoDB(connectionString, database)`, the same `IEntityStore` (keys from
  a counter, a Guid or an ObjectId; optimistic concurrency; transactions on a replica set) and change streams for the caches
  (`notifications: n => n.Watch<T>()`), resumed after a reconnection. It passes the store contract tests in CI.
- Tests that need a server (PostgreSQL, MongoDB) are reported as skipped when its variable is missing, instead of passing
  without running.
- `ChangeUserAsync()` (`IUserScopedData`): caches and the offline copy are forgotten when the user changes, before
  anything is loaded for the next one; refused while offline writes wait. `OfflineMode.DiscardPendingChangesAsync`.
- Traces and metrics: `QueryeablesDiagnostics` (`ActivitySource` and `Meter` named `Queryeables`) for remote calls,
  server requests, caches, published changes and offline synchronizations.
- Offline storage: `IndexedDbOfflineStorage` (new package `Queryeables.Browser`) and `SqliteOfflineStorage`
  (`Queryeables.Sqlite`), with contract tests shared by every storage.
- `Samples/Queryeables.OfflineDemo`: one server, two clients, a network cut, a conflict; run by the CI.
- `IChangeFeed.PublishReload()`: caches reload everything when the source of their data changes (offline copy or server).
- Security tests from a hostile client: rows of another user through a graph, by key, with conditions written by hand,
  and after a change of user.
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
- "Keep mine" on a conflict could insert a row twice: when the answer of its write was lost and the user kept the
  offline write again, the row deleted on the server meanwhile was inserted again. The write now carries an operation id
  of its own, the same for every attempt.
- The offline synchronization could apply a write twice: when the answer was lost after the server committed it, or when
  the client stopped before recording it, the write was sent again and an insert created a second row. Writes now carry
  the id of their queue entry, the server answers a repeated id with the first result, and each answer is recorded at
  once.
- A row saved offline could vanish from the copy after a restart: when writing the list of keys failed, the next save
  did not write it again unless another row came or went. The list is now marked saved only once written.
- A change of user could forget an offline write queued at the same moment: the check is now made again under the lock
  where writes are queued, and the offline mode is asked before the caches are emptied.
- The changes since a date (`GetChangesAsync`) were read for every user, then filtered in memory, and the limit was
  checked on a count that left out soft-deleted rows: a client could make the server read a whole table. The row filter
  and the limit are now part of the query.
- `Queryeables.EntityFramework` did not treat warnings as errors: the rule is now set once, in `Directory.Build.props`,
  for every library.
- With `[SoftDelete]`, an update of a deleted row brought it back to life, and deleting it again succeeded: both now find
  nothing to write (`false`), as for a row that does not exist.
- After going online again, a cache could keep rows created offline under their temporary key: switching modes now
  reloads the caches instead of synchronizing them.
- `FileOfflineStorage` could mix up keys that differ only by case on Windows and macOS (`Ana` and `ana`), and drop a
  trailing dot on Windows; long keys now get a hashed file name.
- EF Core store: `EfEntityStore`, `EfConnectionExtensions` and `UseEntityFramework` carry `RequiresUnreferencedCode` and
  `RequiresDynamicCode`, like EF Core itself: the build has no trimming warnings, and a trimmed or Native AOT application
  is warned where it uses them.
- SQLite: changes are reported once the commit has returned, not from inside the commit hook, so that a cache reading the
  row again never sees its previous state.

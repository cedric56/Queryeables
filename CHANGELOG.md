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
  | `Queryeables.Onion`, `Queryeables.Onion.Client`, `Queryeables.Server` (packages and namespaces) | `Queryeables.MagicOnion`, `Queryeables.MagicOnion.Client`, `Queryeables.MagicOnion.Server` |
- Changes reported by clients to a hub are no longer relayed by default (`ChangeHubOptions.RelayClientChanges`).
- The SQL log (`logCommands`) is written at `Debug` instead of `Information`, without the parameter values unless
  `logParameterValues: true`.
- The MagicOnion server reads entities as untrusted MessagePack data (`MessagePackEntitySerializer.Server`).
- The idempotency key of a write includes its type and kind; a user with no stable identity (authenticated, but neither
  a name identifier nor a name) cannot send idempotent writes.

### Added
- `Queryeables.Replication`: `EntityReplicationStore`, the log, merged documents and replicas of a Routeables Remote
  Protocol server (`Routeables.Remote`) in any Queryeables store, one transaction per decision; `ReplicationProjection<T>`
  writes the merged documents to the application's entities. Tested on memory, EF Core (SQLite) and MongoDB.
- `Queryeables.Templates`: `dotnet new queryeables-console` and `queryeables-client-server`. The CI installs them from the
  packages it has just made, generates both outside the repository, builds and runs them.
- A [tutorial](docs/tutorial.md) in four steps on one application (local, cache, graph, server and offline), and the way to
  use the packages before they are published (a local package source, in [getting started](docs/getting-started.md)).
- `[NotQueryable]` and `[ServerOwned]`; columns left out by the serializer can no longer be filtered or sorted on.
- `EntityServerBuilder.DetailedErrors` and `MaxBatchSize`; `ChangeHubOptions` (client changes, rate, queue, key length);
  `CertificateValidation` for the transports; `DbFactoryOptions.LogParameterValues`.
- `SECURITY.md`, CodeQL analysis, Dependabot for NuGet, SourceLink and deterministic CI builds, a `nuget.config` that maps
  every package to nuget.org.
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
- Second audit (November 2026):
  - A remote update erased the columns the serializer leaves out (a password hash): they now keep their stored values,
    like `[ServerOwned]`.
  - A property hidden with an attribute the serializer ignores (`[JsonIgnore]` under MessagePack) was sent to clients: the
    server now refuses to serve such a type and logs why. `IEntitySerializer.Omits(PropertyInfo)` says what a serializer
    leaves out.
  - The children of a graph sent by a client bypassed `[ServerOwned]` and the policy and row filter of their own type:
    each child is now checked as its type (`IGraphWriteGuard`), and removing a parent checks the deletion of each child.
  - Dommel: `%`, `_` and `[` in `Contains`, `StartsWith`, `EndsWith` were wildcards (now literal, `escape '!'`); on
    SQLite they ignored case (now `instr`/`substr`, case-sensitive as elsewhere); `!=` on a nullable column left out
    the rows where it is null (now `(c <> @p or c is null)`, as in C#); a predicate always true (`c => true`) wrote
    `WHERE True`, refused by SQL Server (now no `WHERE`; one always false reads nothing).
  - EF Core on SQLite: `StartsWith` and `EndsWith` ignored case (`LIKE`); they now compare `substr`, as C# does.
  - MongoDB: the key counter ran inside the transaction, so two concurrent inserts failed with a WriteConflict; it now
    runs outside, as a database sequence does (a rollback leaves a gap).
  - Offline mode: a stop between writing a queued write and the index of the queue could lose it; new entries are now
    recorded before the index (a write-ahead list), and the queue is saved before the local copy.
  - A numeric key sent by a client with an insert is reset: the database numbers the row
    (`[DatabaseGenerated(DatabaseGeneratedOption.None)]` keeps a key the application assigns).
  - The change hubs (MagicOnion, SignalR) refuse anonymous connections unless `ChangeHubOptions.AllowAnonymous` is set.
  - The row filter of a write is interpreted, not compiled, on every call.
  - The CI builds a pinned commit of Routeables.SignalR; without that repository next to this one, `Queryeables.Replication`
    and its tests are left out of a local build (a warning; an error in the CI).
- The build: `Queryeables.Replication` references `Routeables.Remote` in the Routeables.SignalR repository next to this one
  (`RouteablesRemoteRoot`), the path the CI provides too; the solution lists that project, as Visual Studio needs.
- A remote client could make the server read a whole table with a sorted page on Dommel (SQLite, PostgreSQL, SQL Server):
  `DommelEntityStore` sorts and pages in the database (ORDER BY with LIMIT or OFFSET/FETCH, the key breaking ties), and
  the server bounds a sorted page on a store that sorts in memory to `maxReadSize` rows (`IEntityStore.SortsInStore`).
- An update checked against the stored row (row filter, `[ServerOwned]` values) reads it and writes it in one
  transaction.
- A commit of a `RemoteEntityStore` carries an operation id, and is sent again with it when the call fails
  (`BatchRetries`, 2): the server applies it once (`IEntityTransport.ExecuteBatchOnceAsync`, MagicOnion included).
- `!(e.Flag == true)` on a `bool?` is refused (as an ordering on a nullable is): the server and the offline copy would
  disagree on null. Write `e.Flag != true`.
- An anonymous user owns no hub connection: another anonymous client cannot keep a change from it.
- SQLite: a change of a table whose key is not its rowid (`Id INT PRIMARY KEY`) is reported without a key, so that its
  caches synchronize instead of reading another row; a commit made outside the stores is reported again after one second.
- A change feed without `[Table]` also answers to Dommel's plural (`Person` → `Persons`).
- PostgreSQL: notifications carry the schema (two tables of one name in two schemas are told apart) and the key column
  only, not the whole row.
- `Queryeables.Server/`, left over from the renaming to `Queryeables.MagicOnion.Server`, is removed.
- Security, from the November audit:
  - On Android, the MagicOnion and SignalR clients accepted any server certificate when no validator was given
    (interception). The system now validates it on every platform.
  - Elsewhere, `validateCertificate` was silently ignored (MagicOnion: never attached to the `SocketsHttpHandler`;
    SignalR: Android only). It is now applied on every platform, to the HTTP requests and the WebSocket, and a handler
    that cannot use it is an error.
  - A client could make up changes on a hub: a "delete" that removed a row from the other caches, or a "reload
    everything" sent in a loop to every client (each reloading its table from the server). Client changes are off by
    default, and checked and rate-limited when on.
  - A client could name another user's connection on a write, and keep the change from that user; the connections to
    leave out were sent to every client. The name is now believed only for the caller's own connection, and never sent.
  - The hubs' queue had no bound. It now holds 10,000 changes, beyond which the clients synchronize instead.
  - A property the client cannot read could be guessed through filters and sorts (`StartsWith "a"`, `"ab"`...).
  - A client could set any column on a write; `[ServerOwned]` keeps what the server decides.
  - Unexpected errors sent the database's message to the client (tables, constraints, values). The client now gets a
    reference, and the message is logged on the server.
  - A filter could hold a million values (1000 conditions of 1000), and 1000 conditions built a tree 1000 levels deep for
    the stores' recursive visitors. A filter now holds 2000 values in all, and its tree is balanced.
  - A batch had no size limit (one huge transaction); a page number near `int.MaxValue` overflowed the offset.
  - The children of a graph were not validated (`[Required]`, `[StringLength]`...): only the parent was.
  - A hub's `RemoveAsync` left the connection in its MagicOnion group.
- Reliability:
  - An offline write whose answer was lost could have a later change on the same row merged into it: the server then
    answered the retry with the first result, and the later change was lost. A write sent once is no longer merged; a
    later insert's temporary key is replaced by the server's in the writes queued after it.
  - `MemoryIdempotencyStore` sorted all its entries on every write once full; it now trims in batches.
  - A row tracked by `EntityCache.Track` during a reload could vanish from the cache.
  - `FileOfflineStorage` shared one temporary file between writers of a key and did not flush it before renaming it.
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

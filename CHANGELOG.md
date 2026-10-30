# Changelog

All notable changes are listed here. Versions follow [Semantic Versioning](https://semver.org); until 1.0, a minor version
may change the API.

## [Unreleased]

First public version.

### Added
- `IEntityStore` on Dommel, EF Core and a remote server, with transactions that return a `DbResult`.
- Providers: SQLite (with change notifications), PostgreSQL, any Dommel database, EF Core, MagicOnion client.
- `[EntityGraph]`: parents saved, removed and loaded with their children; `[EntityCache]`: caches that follow the database.
- Source generator: graph services, registration (`AddGeneratedEntities()`, including referenced projects), QRY001-QRY009.
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
- Dommel store: `e => e.Column == value` (reads by key and by foreign key) runs SQL built once per type and column.

### Fixed
- SQLite: changes are reported once the commit has returned, not from inside the commit hook, so that a cache reading the
  row again never sees its previous state.

# Glossary

The names an application meets, from the most common to the least.

| Name | What it is | Where |
| --- | --- | --- |
| **Repository** (`I{Parent}Repository`) | A parent with its children: `SaveAsync` (one transaction), `GetAsync(key)` (with its children), `RemoveAsync`. Generated from `[EntityGraph]`; a `GraphRepository` underneath. | [Entity graphs](guides/entity-graphs.md) |
| **Cache** (`I{Entity}Cache`) | The rows of a type in memory, kept up to date: by the writes of the application at once, by the database's notifications for the others. Generated from `[EntityCache]`; `IEntityCache<TEntity, TKey>` underneath. | [Caches](guides/caches-and-sync.md) |
| **Query** (`stores.Query<T>()`) | A read with filters, sorts, pages and projections, run by the database or the server. | [Stores](guides/stores-and-transactions.md#composed-queries) |
| **Store** (`IEntityStore`) | One connection: CRUD, transactions. Created by the store factory, disposed by its user; the repositories and caches use it for you. | [Stores](guides/stores-and-transactions.md) |
| **Store factory** (`IEntityStoreFactory`) | What creates the stores: registered by the provider (`UseSqlite`, `UseMagicOnion`...). | [Stores](guides/stores-and-transactions.md) |
| **EntityResult** | The outcome of a write: `Successful`, or an error kind (`Validation`, `Conflict`, `NotFound`, `Forbidden`...). `.OrThrow()` makes it an exception; `ThrowOnFailure()` makes the repositories throw. | [Stores](guides/stores-and-transactions.md) |
| **Provider** | Where the stores come from: `UseSqlite`, `UseNpgsql`, `UseDommel`, `UseEntityFramework`, `UseMongoDB`, `UseMagicOnion`. | [Getting started](getting-started.md#which-package) |
| **Change feed** (`IChangeFeed<T>`) | The changes of a table, reported by the database (SQLite hooks, PostgreSQL triggers, MongoDB change streams): what keeps a cache current. | [Change notifications](guides/change-notifications.md) |
| **Aggregate repository** (`IAggregateRepository<T>`) | What a server calls to save an entity with its children: the generated repository, or your own service. | [Remote access](guides/remote-access.md) |
| **Dispatcher** (`EntityRequestDispatcher`) | The server's entry point: checks the policy and the rows of each request, then calls the stores. The transport calls it. | [Remote access](guides/remote-access.md) |
| **Access policy** (`IEntityAccessPolicy`) | Who may read and write a type on a server; a row filter (`RowLevelSecurity`) says which rows. | [Security](guides/security.md) |
| **Offline mode** (`OfflineMode`) | A copy of the server's rows on the client and a queue of the writes made without the server. | [Offline mode](guides/offline-mode.md) |

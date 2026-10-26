# Queryeables

**A modular data-access and synchronization toolkit for .NET.**

Queryeables provides a common abstraction over data stores, with
optional local entity caching, incremental synchronization, remote data
access, and change notifications. It is designed for applications that
need to share the same data-access patterns across different persistence
providers and, optionally, across a client/server boundary.

> **Status:** Queryeables targets .NET 10. Check the repository's
> releases, package publishing status, and test results before adopting
> it in production.

## Why Queryeables?

Applications often end up coupling business logic to a specific ORM or
writing custom infrastructure for local caches, remote access, and
change propagation. Queryeables brings these concerns behind a set of
composable abstractions.

-   **Provider abstraction:** use a common `IEntityStore` API with the
    included Entity Framework Core and Dommel integrations.
-   **Local entity cache:** maintain an in-memory, key-indexed view of
    entities.
-   **Incremental synchronization:** when entities expose the expected
    synchronization metadata, refresh changes since the last
    synchronization instead of always reloading everything.
-   **Change notifications:** connect data changes to cache refreshes
    and client notifications.
-   **Remote access:** expose entity operations through the included
    MagicOnion/gRPC infrastructure, with a separate SignalR-based
    notification option.
-   **Controlled remote operations:** configure access policies,
    validate writes, restrict supported filter operations, and cap page
    sizes.
-   **Transactional helpers:** group writes and synchronize child
    collections through reusable store extensions.

## Architecture

The repository is split into focused projects so an application can
reference only the pieces it needs.

  -----------------------------------------------------------------------
  Project                             Purpose
  ----------------------------------- -----------------------------------
  `Queryeables.Abstraction`           Core contracts, result types,
                                      entity metadata, store and cache
                                      abstractions.

  `Queryeables.EntityFramework`       `IEntityStore` implementation based
                                      on Entity Framework Core relational
                                      APIs.

  `Queryeables.Dommel`                `IEntityStore` implementation based
                                      on Dommel.

  `Queryeables.Onion`                 Remote entity handling, access
                                      policies, request dispatching, and
                                      filter translation.

  `Queryeables.Onion.Client`          Client-side MagicOnion/gRPC
                                      integration and change-hub
                                      subscription.

  `Queryeables.Server`                Server-side MagicOnion/gRPC
                                      services.

  `Queryeables.SignalR`               SignalR client integration and
                                      MessagePack protocol support.

  `Queryeables.SignalR.Server`        Server-side SignalR hub
                                      integration.

  `Queryeables.PostgreSql`            PostgreSQL-related integration
                                      built on Npgsql and Dommel.

  `Queryeables.Sqlite`                SQLite-related integration.

  `Queryeables.SqlDependency`         Database change monitoring
                                      integration using
                                      SqlTableDependencyCore.

  `Queryeables.Tests`                 Automated tests for the solution.
  -----------------------------------------------------------------------

The optional components are not all required. For example, an
application using only a local store abstraction and EF Core does not
need the remote transport projects.

## Requirements

-   .NET SDK 10.0
-   A supported data provider for the persistence integration you choose
-   For remote features, the corresponding server/client transport
    configuration

The project files enable nullable reference types and implicit usings.
Several library projects also declare AOT compatibility; validate
trimming and Native AOT behavior for your application's actual
dependency graph before relying on it.

## Getting started

### 1. Get the source

``` bash
git clone https://github.com/cedric56/Queryeables.git
cd Queryeables
```

### 2. Build and test

``` bash
dotnet restore
dotnet build
dotnet test
```

Run these commands from the repository root. If the repository's target
framework or SDK requirements change, follow the project files and CI
configuration.

### 3. Reference the projects you need

If packages are not published for the version you want to use, add
project references to your application. For example, to use the
abstractions and Entity Framework Core implementation:

``` xml
<ItemGroup>
  <ProjectReference Include="path/to/Queryeables.Abstraction/Queryeables.Abstraction.csproj" />
  <ProjectReference Include="path/to/Queryeables.EntityFramework/Queryeables.EntityFramework.csproj" />
</ItemGroup>
```

Replace `path/to/` with the relative path from your application project.
Add the Dommel, transport, or database integration projects only when
needed.

## Core concepts

### `IEntityStore`

`IEntityStore` is the persistence boundary. It provides a shared set of
operations for reading, selecting, paging, inserting, updating,
deleting, counting, and working with transactions. Provider-specific
implementations translate those operations to the underlying persistence
technology.

Composite helpers in `EntityStoreExtensions` include:

-   `RunInTransactionAsync` --- execute work in a transaction and report
    success or failure through a result object.
-   `InsertManyAsync`, `UpdateManyAsync`, and `DeleteManyAsync` ---
    perform batches of operations in a transaction.
-   `SyncChildrenAsync` --- reconcile a collection of child entities
    with a desired collection.
-   `GetAsync` --- retrieve an entity or return a not-found error.

Consult the interfaces and XML documentation in the source for the exact
signatures and provider-specific behavior.

### `EntityCache<TEntity, TKey>`

`EntityCache<TEntity, TKey>` keeps entities in a concurrent dictionary
keyed by the key selector supplied at construction. It supports:

-   Initial loading with `LoadAsync`.
-   Lookup with `Find`.
-   Manual updates through `Track`.
-   Synchronization through `SyncAsync`.
-   An `EntitiesChanged` event for consumers.
-   A `RefreshFailed` event when a monitor-triggered refresh fails.

When a monitor is provided, cache refresh behavior depends on the
available metadata and key parsing configuration. The implementation can
update individual rows, synchronize incrementally using a `LastModified`
convention, or reload the cache. Call `SyncAsync` after reconnecting to
reconcile changes that may have occurred while disconnected.

**Important:** an in-memory cache is a snapshot, not a substitute for
database transactions or authorization. Define cache lifetime,
invalidation behavior, and stale-data expectations explicitly in your
application.

### Remote entity access

The remote infrastructure routes requests through registered entity
handlers. An `EntityHandler<TEntity>` is configured with a store factory
and an explicit `IEntityAccessPolicy`. The handler applies authorization
checks and can validate entities before writes.

The API includes policies such as:

-   `EntityAccessPolicies.Authenticated`
-   `EntityAccessPolicies.ReadOnly`
-   `EntityAccessPolicies.AllowAll`
-   `EntityAccessPolicies.From(...)` and `FromAsync(...)` for custom
    rules

`AllowAll` intentionally allows anonymous access and should only be used
when that behavior is appropriate for the deployment. Prefer explicit,
least-privilege policies for exposed endpoints.

The remote filter translator supports a defined subset of LINQ
predicates, including property comparisons, boolean properties,
membership tests, and selected string operations. Unsupported expression
shapes are rejected rather than being sent as arbitrary executable
expressions. Review `Filters.cs` for the exact supported syntax.

### Change notifications

Queryeables includes abstractions and integrations for monitoring data
changes and notifying connected clients. Available pieces include
MagicOnion/gRPC and SignalR integrations, plus optional
database-monitoring integrations.

Notifications are a signal to refresh or reconcile state; they should
not be treated as a durable event log unless the application adds and
verifies those guarantees. Design reconnect and resynchronization
behavior for the failure modes relevant to your system.

## Example: transaction helper

The following illustrates the intended shape of a transaction using an
`IEntityStore`. Adapt the entity and store creation to your provider and
application configuration.

``` csharp
await using var store = CreateStore();

var result = await store.RunInTransactionAsync(async s =>
{
    await s.UpdateAsync(order);
    await s.SyncChildrenAsync(
        previousLines,
        order.Lines,
        line => line.Id == 0,
        (current, previous) => current.Id == previous.Id);
});

if (!result.Successful)
{
    // Handle the reported failure.
}
```

`CreateStore`, `order`, and `previousLines` are application-defined.
Check the actual store interface for the available overloads and
signatures.

## Security considerations

When exposing data through a remote transport:

1.  Register an explicit access policy for every entity handler.
2.  Avoid `AllowAll` on public or untrusted networks.
3.  Validate writes and apply domain-level validation in addition to
    DataAnnotations.
4.  Limit page sizes and consider additional limits for query
    complexity, request size, and execution time.
5.  Expose only the entity types and properties clients are allowed to
    access.
6.  Authenticate and authorize transport connections using your server's
    security configuration.
7.  Test concurrent writes, transaction rollback, reconnects, missed
    notifications, and stale cache behavior.
8.  Do not assume that a change notification is reliable delivery of
    every database event.

The safeguards in the library do not replace application-specific
authorization, tenant isolation, validation, logging, or operational
monitoring.

## Testing

The test project is `Queryeables.Tests`. It references the abstraction,
provider, and remote components and includes SQLite-based tests.

From the repository root:

``` bash
dotnet test Queryeables.Tests/Queryeables.Tests.csproj
```

For production use, also test the exact database provider and transport
configuration used in deployment. Performance and scalability should be
measured with representative data sizes, query shapes, and notification
rates.

## Current scope and limitations

Queryeables is a toolkit rather than a complete application framework.
It does not remove the need to design domain boundaries, authentication,
authorization, schema migrations, observability, deployment, and
recovery procedures.

Before production adoption, verify:

-   The current release and package availability.
-   Compatibility with the exact .NET SDK and provider versions in use.
-   Test results on the target operating system and database.
-   Cache behavior during concurrent updates and reconnects.
-   Remote request limits and authorization rules.
-   Throughput, latency, memory usage, and behavior under backpressure.

## Contributing

Issues, bug reports, and pull requests are welcome through the [GitHub
repository](https://github.com/cedric56/Queryeables).

When reporting a bug, include the .NET SDK version, relevant provider
and transport versions, a minimal reproduction, and the expected versus
actual behavior. Add tests for changes to persistence semantics,
filtering, synchronization, or concurrency.

## License

Check the repository's license file before redistributing or using
Queryeables in a project. If no license is present, ask the maintainer
to clarify the terms before treating the code as open source.

## Acknowledgements

The source repository includes `CREDITS.md` documenting inspirations and
acknowledgements for selected implementation ideas. See that file for
details.

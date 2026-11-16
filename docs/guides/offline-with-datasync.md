# Offline with Datasync

Queryeables has no offline mode of its own: a client that must work without the server keeps a local database synchronized
by the [Datasync Community Toolkit](https://github.com/CommunityToolkit/Datasync), and Queryeables works on that database like
on any EF Core database. Datasync queues the writes, pushes them, pulls the server's changes and resolves the conflicts;
Queryeables brings its stores, caches and graphs.

```bash
dotnet add package Queryeables.Datasync              # Queryeables.EntityFramework and the Datasync client
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
```

## The local database

An `OfflineDbContext` of Datasync, with the entities of the client: a `string` key, `UpdatedAt`, `Version` and, for
soft-deleted rows, `Deleted` (see [the Datasync client](https://communitytoolkit.github.io/Datasync/in-depth/client/)).

```csharp
public class AppDbContext(DbContextOptions<AppDbContext> options) : OfflineDbContext(options)
{
    public DbSet<TodoItem> TodoItems => Set<TodoItem>();

    protected override void OnDatasyncInitialization(DatasyncOfflineOptionsBuilder optionsBuilder)
        => optionsBuilder.UseEndpoint(new Uri("https://server/"));
}
```

## Queryeables on it

```csharp
services.UseQueryeables(q => q
    .UseDatasync(sp => new AppDbContext(options))     // the stores write through the context: Datasync queues them
    .AddGeneratedEntities());

var todos = app.GetRequiredService<ITodoItemCache>();  // [EntityCache]: the rows of the local database, in memory
await todos.LoadAsync();
```

The writes of the application go through the context: Datasync queues them for the next push, and the caches show them
at once. `UpdateWhere` and `DeleteWhere` run in the database without the change tracker: Datasync does not see them, so
write the rows of a synchronized table one by one.

## Synchronizing

The application decides when, with the calls of Datasync; `ThenRefreshCachesAsync` brings the caches up to date with
what a pull wrote into the local database:

```csharp
await using var context = new AppDbContext(options);
var pushed = await context.PushAsync();
var pulled = await context.PullAsync().ThenRefreshCachesAsync(app);
```

`app.RefreshCachesAsync()` does the same after any change made behind Queryeables: only the caches already loaded, and only
the changed rows for the types with `[LastModified]`.

## The server

A Datasync server: its `TableController`s, on its own database, with its access control. The server of Queryeables
(replicas, [remote access](remote-access.md)) and the server of Datasync can live in the same application, on the same
database.

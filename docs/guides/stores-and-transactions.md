# Stores and transactions

## A store is a unit of work

`IEntityStore` is one connection (Dommel), one `DbContext` (EF Core) or one remote session. Register an
`IEntityStoreFactory` (the providers do it), create a store per operation, and dispose it:

```csharp
public sealed class OrderRepository(IEntityStoreFactory stores)
{
    public async Task<IReadOnlyList<Order>> OpenOrdersAsync(long customerId)
    {
        await using var store = stores.Create();
        return await store.SelectAsync<Order>(o => o.CustomerId == customerId && !o.Closed);
    }
}
```

Never register a store itself in the container: it would live as long as its scope, and share one connection between
callers.

## Reading

| Method | Notes |
| --- | --- |
| `GetAllAsync<T>()` | Every row |
| `SelectAsync<T>(predicate)` | Translated by Dommel, EF Core, or sent as conditions to a server |
| `FirstOrDefaultAsync<T>(predicate)`, `GetAsync<T>(predicate)` | `GetAsync` throws `NotFound` instead of returning null |
| `SelectPagedAsync<T>(predicate, page, size)` | Pages start at 1, sorted by key |
| `SelectPagedAsync<T>(predicate, page, size, sort)` | In the given order: `Sort<T>.By(p => p.Name).ThenByDescending(p => p.Id)` |
| `SelectAsync<T>(predicate, sort)` | Every matching row, in the given order |
| `SelectAsync<T, TResult>(predicate, selector)` | A projection: `p => new PersonRow(p.Id, p.Name)` |
| `CountAsync<T>(predicate)` | Counted in the database (by the server, remotely) |
| `SumAsync`, `MinAsync`, `MaxAsync`, `AverageAsync` | `await store.SumAsync<Line, decimal>(l => l.Year == 2026, l => l.Price * l.Quantity)` |
| `GroupAsync<T, TKey>(predicate, key, values)` | `await store.GroupAsync<Order, Status>(o => true, o => o.Status, a => a.Sum(o => o.Total))`: each group with its count and values |
| `AggregateAsync<T>(predicate, aggregation)` | The general form: `Aggregation.Of<T>().GroupBy(...).Count().Sum(...).Max(...)` |
| `SelectAfterAsync<T>(predicate, sort, after, size)` | The page after a row (keyset paging): as deep as wanted, unshifted by inserts; for infinite scrolling |
| `SelectChangesAsync<T>(sinceUtc)` | Rows changed since a date, soft-deleted ones included (`[LastModified]`) |

### Composed queries

`Query<T>()`, on a store or on the factory (a store per execution, disposed after), composes in the LINQ style:

```csharp
var rows = await stores.Query<Person>()
    .Where(p => p.Active)
    .Where(p => p.Name.StartsWith(search))            // several Where are joined with &&
    .OrderBy(p => p.LastName).ThenByDescending(p => p.Id)
    .Skip(50).Take(25)                                 // or .Page(3, 25)
    .Select(p => new PersonRow(p.Id, p.Name))
    .ToListAsync();                                    // FirstOrDefaultAsync, FirstAsync, CountAsync, AnyAsync
```

It runs through the methods above: sorted and paged by EF Core and by a server. `Skip` and `Take` follow LINQ (a `Skip`
after a `Take` shortens it); a `Skip` that is not a multiple of `Take` reads the two pages around it. A query is immutable:
a base query can be shared. It is not an `IQueryable`: joins, grouping and subqueries do not compile, instead of failing
at run time.

Predicates give the rows C# gives, in every store and remotely:

- comparisons between a property and a value, between two properties, or of values computed from them: arithmetic
  (`+ - * / %`), `??`, text (`ToLower`, `ToUpper`, `Trim`, `Length`, `Substring`, `+`, `string.Compare(a, b) > 0`), dates
  (`Year`, `Month`, `Day`, `Hour`, `Minute`, `Second`, `DayOfWeek`, `DayOfYear`, `Date`), `Math.Abs`, `Floor`, `Ceiling`;
- `ids.Contains(e.Id)`, `StartsWith`, `EndsWith` and `Contains` on text (wildcards are text);
- relations declared by the graph: `Any`, `All` and `Count` on a collection, a condition on a navigation
  (see [entity graphs](entity-graphs.md#filters-through-the-relations));
- combined with `&&`, `||` and `!`, nulls as C# treats them (`!(x > 1)` keeps the rows where x is null).

Text compares as the database collation does (case-insensitive by default on SQL Server and MySQL); `Math.Round` is not
translated (rounding differs between C# and SQL).

Where sorting and projections run:

| Store | Sorted pages | Projections |
| --- | --- | --- |
| EF Core | In the database, the key breaking ties | In the database: only the selected columns are read |
| Remote | By the server, with its own store; only the page crosses the network | The server sends only the properties the selector reads; the selector runs on the client |
| Dommel | In the database (ORDER BY, then LIMIT or OFFSET/FETCH), the key breaking ties | Only the columns the selector reads, then in memory |
| Your own store | In memory, after the filtered read (`SortsInStore` is false): a server reads at most `maxReadSize` rows for it | In memory, after the read |

A sort names properties of the entity, at most 8; a server checks them against the exposed type.

## Writing

`InsertAsync` writes the generated key back into the entity. `UpdateAsync` and `DeleteAsync` return false when the row no
longer exists. `SaveAsync(entity, e => e.Id)` inserts when the key has its default value, updates otherwise.

With an `int`/`long` property marked `[ConcurrencyCheck]`, updates and deletes check the version and increment it: a
stale entity throws `EntityStoreException` with `EntityErrorCode.Conflict`.

## Transactions

```csharp
EntityResult result = await store.RunInTransactionAsync(async s =>
{
    await s.InsertAsync(order);
    await s.InsertManyAsync(lines);
});

if (!result.Successful)
    logger.LogWarning("{Code}: {Message} {Errors}", result.ErrorCode, result.Exception, result.Errors);
```

- An exception inside the work, or a failed commit, becomes a failed `EntityResult`; the transaction is rolled back.
- Cancellation is not a failure: `OperationCanceledException` propagates.
- `RunInTransactionAsync<T>` returns a `EntityResult<T>` with the value of the work.
- `.OrThrow()` on the task turns a failure into an `EntityStoreException`, for code that prefers exceptions.

`InsertManyAsync`, `UpdateManyAsync` and `DeleteManyAsync` run in one transaction: if one row fails, none is written.

### Many rows at once

```csharp
var archived = await store.UpdateWhereAsync<Invoice>(i => i.Year == 2024, set => set.Set(i => i.Archived, true));
var purged = await store.DeleteWhereAsync<Draft>(d => d.Created < limit);
```

Each row is written as an update or a delete: versions, `[LastModified]`, soft delete, validation and the access rules of
a server apply, and each row is notified. Remotely, the server does it in one call and one transaction (at most
`maxReadSize` rows, as a read). Run it in a transaction locally to make it atomic.

### A patch: only what changed

```csharp
var original = EntityValues.Copy(customer);        // as read
customer.Phone = "...";                            // edited in a form
await store.PatchAsync(original, customer);
```

Only the changed columns are written over the stored row: what others changed meanwhile in other columns is kept, and
`customer` then holds the stored row. A column changed both here and by someone else is a `Conflict`. Remotely, only the
changed values travel, and the server never takes from them the key, the version, `[ServerOwned]` properties or those the
serializer leaves out.

## Sync conventions

`q.WithSyncConventions()` wraps every store in `SyncConventionsDecorator`:

- `[LastModified]` (`DateTime` UTC or `DateTimeOffset`) is set on every insert, update and soft delete;
- `[SoftDelete]` (`bool`): `DeleteAsync` sets it instead of deleting, and reads hide the row; `SelectChangesAsync` still
  returns it, so that synchronizing clients learn about the deletion.

On a remote client, apply the conventions on the server only: the server's clock dates every change.

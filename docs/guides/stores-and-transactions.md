# Stores and transactions

## A store is a unit of work

`IEntityStore` is one connection (Dommel), one `DbContext` (EF Core) or one remote session. Register an
`IEntityStoreFactory` (the providers do it), create a store per operation, and dispose it:

```csharp
public sealed class OrderService(IEntityStoreFactory stores)
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
| `CountAsync<T>(predicate)` | Counted in the database |
| `SelectChangesAsync<T>(sinceUtc)` | Rows changed since a date, soft-deleted ones included (`[LastModified]`) |

Predicates stay simple so that every store understands them: comparisons, booleans, `ids.Contains(e.Id)`, and
`StartsWith`, `EndsWith`, `Contains` on strings, combined with `&&`, `||`, parentheses and `!`. A `!` is pushed down to the
terms (`!(a && b)` is sent as `!a || !b`); it cannot negate a `Contains`, a text search, or an ordering on a nullable
property. For joins or grouping, use the ORM directly.

Where sorting and projections run:

| Store | Sorted pages | Projections |
| --- | --- | --- |
| EF Core | In the database, the key breaking ties | In the database: only the selected columns are read |
| Remote | By the server, with its own store; only the page crosses the network | On the client, after reading the entities (no expression goes over the wire) |
| Dommel, your own store | In memory, after the filtered read | In memory, after the read |

A sort names properties of the entity, at most 8; a server checks them against the exposed type.

## Writing

`InsertAsync` writes the generated key back into the entity. `UpdateAsync` and `DeleteAsync` return false when the row no
longer exists. `SaveAsync(entity, e => e.Id)` inserts when the key has its default value, updates otherwise.

With an `int`/`long` property marked `[ConcurrencyCheck]`, updates and deletes check the version and increment it: a
stale entity throws `EntityStoreException` with `EntityErrorCode.Conflict`.

## Transactions

```csharp
DbResult result = await store.RunInTransactionAsync(async s =>
{
    await s.InsertAsync(order);
    await s.InsertManyAsync(lines);
});

if (!result.Successful)
    logger.LogWarning("{Code}: {Message} {Errors}", result.ErrorCode, result.Exception, result.Errors);
```

- An exception inside the work, or a failed commit, becomes a failed `DbResult`; the transaction is rolled back.
- Cancellation is not a failure: `OperationCanceledException` propagates.
- `RunInTransactionAsync<T>` returns a `DbResult<T>` with the value of the work.
- `.OrThrow()` on the task turns a failure into an `EntityStoreException`, for code that prefers exceptions.

`InsertManyAsync`, `UpdateManyAsync` and `DeleteManyAsync` run in one transaction: if one row fails, none is written.

## Sync conventions

`q.WithSyncConventions()` wraps every store in `SyncConventionsStore`:

- `[LastModified]` (`DateTime` UTC or `DateTimeOffset`) is set on every insert, update and soft delete;
- `[SoftDelete]` (`bool`): `DeleteAsync` sets it instead of deleting, and reads hide the row; `SelectChangesAsync` still
  returns it, so that synchronizing clients learn about the deletion.

On a remote client, apply the conventions on the server only: the server's clock dates every change.

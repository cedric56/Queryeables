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
| `CountAsync<T>(predicate)` | Counted in the database |
| `SelectChangesAsync<T>(sinceUtc)` | Rows changed since a date, soft-deleted ones included (`[LastModified]`) |

Predicates stay simple so that every store understands them: comparisons, booleans, `ids.Contains(e.Id)`, and
`StartsWith`, `EndsWith`, `Contains` on strings, all joined by `&&` or all by `||`. For joins, projections or grouping, use
the ORM directly.

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

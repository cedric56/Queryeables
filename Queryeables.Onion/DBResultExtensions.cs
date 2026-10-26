using Queryeables.Abstraction;

namespace Queryeables.Onion;

public static class DBResultExtensions
{
    /// <summary>
    /// Like <see cref="EntityStoreExtensions.RunInTransactionAsync"/>, but returns a <see cref="DBResult"/>
    /// instead of throwing: handy in a MagicOnion service (replaces the former DbTransaction.RunAsync).
    /// </summary>
    public static Task<DBResult> RunInTransactionAsDbResultAsync(this IEntityStore store,
        Func<IEntityStore, Task> work, CancellationToken ct = default)
        => DBResult.RunAsync(async () =>
        {
            await store.RunInTransactionAsync(work, ct);
            return null;
        });
}

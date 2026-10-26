using Queryeables.Abstraction;

namespace Queryeables.Onion;

public static class DBResultExtensions
{
    /// <summary>
    /// Comme <see cref="EntityStoreExtensions.RunInTransactionAsync"/>, mais renvoie un <see cref="DBResult"/>
    /// au lieu de lever une exception : pratique dans un service MagicOnion (remplace l'ancien DbTransaction.RunAsync).
    /// </summary>
    public static Task<DBResult> RunInTransactionAsDbResultAsync(this IEntityStore store,
        Func<IEntityStore, Task> work, CancellationToken ct = default)
        => DBResult.RunAsync(async () =>
        {
            await store.RunInTransactionAsync(work, ct);
            return null;
        });
}

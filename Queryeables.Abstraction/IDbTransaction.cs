using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Abstraction;

/// <summary>
/// Transactional save and removal of an aggregate (an entity with its children), for application services.
/// Each call runs in its own transaction and reports its outcome as a <see cref="DbResult"/> instead of throwing.
/// </summary>
/// <remarks>
/// Not to be confused with <see cref="System.Data.IDbTransaction"/>: this generic interface describes a service,
/// not an ADO.NET transaction. Implementations usually rely on
/// <see cref="EntityStoreExtensions.RunInTransactionAsync(IEntityStore, Func{IEntityStore, Task}, CancellationToken)"/>.
/// </remarks>
/// <example>
/// <code>
/// public interface IPersonService : IDbTransaction&lt;Person&gt;
/// {
///     Task LoadContactsAsync(Person person, CancellationToken ct = default);
/// }
///
/// public sealed class PersonService(DbProviderFactory factory) : IPersonService
/// {
///     public async Task&lt;DbResult&gt; SaveAsync(Person person, CancellationToken ct = default)
///     {
///         await using var store = DommelEntityStore.Create(factory);
///         return await store.RunInTransactionAsync(async s =>
///         {
///             if (person.Id == 0) await s.InsertAsync(person, ct);
///             else await s.UpdateAsync(person, ct);
///             // ... children, for example with SyncChildrenAsync
///         }, ct);
///     }
///     // RemoveAsync, LoadContactsAsync...
/// }
/// </code>
/// </example>
public interface IDbTransaction<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity> where TEntity : class
{
    /// <summary>Inserts or updates <paramref name="entity"/> (and its children) in a single transaction.</summary>
    Task<DbResult> SaveAsync(TEntity entity, CancellationToken ct = default);

    /// <summary>Removes <paramref name="entity"/> (and its children) in a single transaction.</summary>
    Task<DbResult> RemoveAsync(TEntity entity, CancellationToken ct = default);
}

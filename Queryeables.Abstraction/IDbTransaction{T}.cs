using System.Data;

namespace Queryeables.Abstraction;

public interface IDbTransaction<T> : IDbTransaction where T : class
{
    Task<DBResult> SaveAsync(T entity, CancellationToken ct = default);
    Task<DBResult> RemoveAsync(T entity, CancellationToken ct = default);
}

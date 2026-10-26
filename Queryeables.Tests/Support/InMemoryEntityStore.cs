using Queryeables.Abstraction;
using System.Linq.Expressions;

namespace Queryeables.Tests;

/// <summary>Store en mémoire pour les tests sans base de données. Ne gère que <see cref="TestClient"/>.</summary>
public sealed class InMemoryEntityStore : IEntityStore
{
    private readonly List<TestClient> _rows;
    private readonly List<string>? _log;

    public InMemoryEntityStore(List<TestClient> rows, List<string>? log = null)
    {
        _rows = rows;
        _log = log;
    }

    public int SelectCalls { get; private set; }

    public Task<IReadOnlyList<T>> GetAllAsync<T>(CancellationToken ct = default) where T : class
    {
        _log?.Add("GETALL");
        return Task.FromResult<IReadOnlyList<T>>(_rows.Select(r => (T)(object)r.Copy()).ToList());
    }

    public Task<IReadOnlyList<T>> SelectAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class
    {
        SelectCalls++;
        var match = predicate.Compile();
        return Task.FromResult<IReadOnlyList<T>>(_rows.Select(r => (T)(object)r.Copy()).Where(match).ToList());
    }

    public async Task<T?> FirstOrDefaultAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class
    {
        _log?.Add("FIRST");
        return (await SelectAsync(predicate, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<T>> SelectPagedAsync<T>(Expression<Func<T, bool>> predicate, int pageNumber, int pageSize, CancellationToken ct = default) where T : class
    {
        Paging.Validate(pageNumber, pageSize);
        return (await SelectAsync(predicate, ct)).Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
    }

    public Task InsertAsync<T>(T entity, CancellationToken ct = default) where T : class
    {
        var client = (TestClient)(object)entity;
        if (client.Id == 0)
            client.Id = _rows.Count == 0 ? 1 : _rows.Max(r => r.Id) + 1;
        _rows.Add(client.Copy());
        _log?.Add($"INSERT {client.Nom}");
        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync<T>(T entity, CancellationToken ct = default) where T : class
    {
        var client = (TestClient)(object)entity;
        _log?.Add($"UPDATE {client.Id}");
        var index = _rows.FindIndex(r => r.Id == client.Id);
        if (index < 0)
            return Task.FromResult(false);
        _rows[index] = client.Copy();
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync<T>(T entity, CancellationToken ct = default) where T : class
    {
        var client = (TestClient)(object)entity;
        _log?.Add($"DELETE {client.Id}");
        return Task.FromResult(_rows.RemoveAll(r => r.Id == client.Id) > 0);
    }

    public Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        _log?.Add("BEGIN");
        return Task.FromResult<IEntityTransaction>(new Transaction(_log));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Transaction(List<string>? log) : IEntityTransaction
    {
        public Task CommitAsync(CancellationToken ct = default) { log?.Add("COMMIT"); return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken ct = default) { log?.Add("ROLLBACK"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { log?.Add("DISPOSE"); return ValueTask.CompletedTask; }
    }
}

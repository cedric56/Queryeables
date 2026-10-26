using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Tests;

/// <summary>
/// In-memory store for any entity type with a <c>long Id</c> key, for tests without a database.
/// Rows are copied in and out, like a database; a transaction not committed restores the previous rows.
/// </summary>
public sealed class MemoryStore(MemoryDatabase database) : IEntityStore
{
    private readonly MemoryDatabase _database = database;
    private Dictionary<Type, List<object>>? _snapshot;

    public Task<IReadOnlyList<T>> GetAllAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(CancellationToken ct = default) where T : class
        => SelectAsync<T>(_ => true, ct);

    public Task<IReadOnlyList<T>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class
    {
        var match = predicate.Compile();
        IReadOnlyList<T> rows = _database.Rows<T>().Cast<T>().Select(Copy).Where(match).ToList();
        return Task.FromResult(rows);
    }

    public async Task<T?> FirstOrDefaultAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class
        => (await SelectAsync(predicate, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<T>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(Expression<Func<T, bool>> predicate, int pageNumber, int pageSize, CancellationToken ct = default) where T : class
        => (await SelectAsync(predicate, ct)).Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();

    public Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T entity, CancellationToken ct = default) where T : class
    {
        var rows = _database.Rows<T>();
        var id = IdProperty<T>();
        id.SetValue(entity, rows.Count == 0 ? 1L : rows.Max(r => (long)id.GetValue(r)!) + 1);
        rows.Add(Copy(entity));
        _database.Log.Add($"INSERT {typeof(T).Name} {id.GetValue(entity)}");
        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T entity, CancellationToken ct = default) where T : class
    {
        var rows = _database.Rows<T>();
        var index = IndexOf(rows, entity);
        _database.Log.Add($"UPDATE {typeof(T).Name} {IdProperty<T>().GetValue(entity)}");
        if (index < 0)
            return Task.FromResult(false);
        rows[index] = Copy(entity);
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T entity, CancellationToken ct = default) where T : class
    {
        var rows = _database.Rows<T>();
        var index = IndexOf(rows, entity);
        _database.Log.Add($"DELETE {typeof(T).Name} {IdProperty<T>().GetValue(entity)}");
        if (index < 0)
            return Task.FromResult(false);
        rows.RemoveAt(index);
        return Task.FromResult(true);
    }

    public Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        _snapshot = _database.Snapshot();
        _database.Log.Add("BEGIN");
        return Task.FromResult<IEntityTransaction>(new Transaction(this));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static int IndexOf<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(List<object> rows, T entity)
    {
        var id = IdProperty<T>();
        var key = id.GetValue(entity);
        return rows.FindIndex(r => Equals(id.GetValue(r), key));
    }

    private static PropertyInfo IdProperty<[DynamicallyAccessedMembers(EntityMembers.Required)] T>()
        => typeof(T).GetProperty("Id") ?? throw new InvalidOperationException($"{typeof(T).Name} has no Id property.");

    /// <summary>Shallow copy of the scalar properties: collections are not stored, like in a table.</summary>
    private static T Copy<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T source) where T : class
    {
        var copy = (T)Activator.CreateInstance(source.GetType())!;
        foreach (var property in typeof(T).GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            if (property.PropertyType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType))
                continue;
            property.SetValue(copy, property.GetValue(source));
        }
        return copy;
    }

    private sealed class Transaction(MemoryStore store) : IEntityTransaction
    {
        private bool _done;

        public Task<DBResult> CommitAsync(CancellationToken ct = default)
        {
            _done = true;
            store._snapshot = null;
            store._database.Log.Add("COMMIT");
            return Task.FromResult(DBResult.Success());
        }

        public Task<DBResult> RollbackAsync(CancellationToken ct = default)
        {
            Restore();
            return Task.FromResult(DBResult.Success());
        }

        public ValueTask DisposeAsync()
        {
            if (!_done)
                Restore();
            return ValueTask.CompletedTask;
        }

        private void Restore()
        {
            _done = true;
            if (store._snapshot is { } snapshot)
                store._database.Restore(snapshot);
            store._snapshot = null;
            store._database.Log.Add("ROLLBACK");
        }
    }
}

/// <summary>Tables shared by the <see cref="MemoryStore"/> instances of a test.</summary>
public sealed class MemoryDatabase
{
    private Dictionary<Type, List<object>> _tables = [];

    public List<string> Log { get; } = [];

    public List<object> Rows<T>()
    {
        if (!_tables.TryGetValue(typeof(T), out var rows))
            _tables[typeof(T)] = rows = [];
        return rows;
    }

    public IReadOnlyList<T> All<T>() => Rows<T>().Cast<T>().ToList();

    internal Dictionary<Type, List<object>> Snapshot() => _tables.ToDictionary(t => t.Key, t => t.Value.ToList());

    internal void Restore(Dictionary<Type, List<object>> snapshot) => _tables = snapshot;
}

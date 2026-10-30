using Queryeables.Abstraction;
using Queryeables.Remote;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Queryeables.AotSmoke;

/// <summary>Entities as JSON through the source-generated context.</summary>
internal sealed class SmokeSerializer : IEntitySerializer
{
    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Info<T>());

    public T? Deserialize<T>(byte[] data) => JsonSerializer.Deserialize(data, Info<T>());

    private static JsonTypeInfo<T> Info<T>()
        => (JsonTypeInfo<T>?)SmokeJson.Default.GetTypeInfo(typeof(T))
           ?? throw new NotSupportedException($"{typeof(T).Name} is not in SmokeJson.");
}

/// <summary>Rows kept in memory, per type; entities are copied in and out through JSON, like a database.</summary>
internal sealed class MemoryDatabase
{
    public object Gate { get; } = new();

    public Dictionary<Type, List<object>> Tables { get; } = [];

    public long NextId { get; set; }
}

/// <summary>A minimal store, enough to drive the library code under Native AOT. Transactions are not isolated.</summary>
internal sealed class MemoryStore(MemoryDatabase database, IEntitySerializer serializer) : IEntityStore
{
    public Task<IReadOnlyList<T>> GetAllAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(CancellationToken ct = default) where T : class
        => SelectAsync<T>(_ => true, ct);

    public Task<IReadOnlyList<T>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(Expression<Func<T, bool>> predicate,
        CancellationToken ct = default) where T : class
    {
        var match = predicate.Compile();      // interpreted under Native AOT
        lock (database.Gate)
            return Task.FromResult<IReadOnlyList<T>>(Rows<T>().Cast<T>().Select(Copy).Where(match).ToList());
    }

    public async Task<T?> FirstOrDefaultAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(Expression<Func<T, bool>> predicate,
        CancellationToken ct = default) where T : class
        => (await SelectAsync(predicate, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<T>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(Expression<Func<T, bool>> predicate,
        int pageNumber, int pageSize, CancellationToken ct = default) where T : class
        => (await SelectAsync(predicate, ct)).Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();

    public Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T entity, CancellationToken ct = default) where T : class
    {
        lock (database.Gate)
        {
            ((IHasId)entity).Id = ++database.NextId;
            Rows<T>().Add(Copy(entity));
        }
        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T entity, CancellationToken ct = default) where T : class
    {
        lock (database.Gate)
        {
            var rows = Rows<T>();
            var index = rows.FindIndex(r => ((IHasId)r).Id == ((IHasId)entity).Id);
            if (index < 0)
                return Task.FromResult(false);
            rows[index] = Copy(entity);
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T entity, CancellationToken ct = default) where T : class
    {
        lock (database.Gate)
            return Task.FromResult(Rows<T>().RemoveAll(r => ((IHasId)r).Id == ((IHasId)entity).Id) > 0);
    }

    public Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => Task.FromResult<IEntityTransaction>(new Transaction());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
    }

    private List<object> Rows<T>()
    {
        if (!database.Tables.TryGetValue(typeof(T), out var rows))
            database.Tables[typeof(T)] = rows = [];
        return rows;
    }

    private T Copy<T>(T entity) => serializer.Deserialize<T>(serializer.Serialize(entity))!;

    private sealed class Transaction : IEntityTransaction
    {
        public Task<DbResult> CommitAsync(CancellationToken ct = default) => Task.FromResult(DbResult.Success());

        public Task<DbResult> RollbackAsync(CancellationToken ct = default) => Task.FromResult(DbResult.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

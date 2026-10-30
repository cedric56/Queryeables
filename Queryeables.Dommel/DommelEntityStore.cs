using Dommel;
using Queryeables.Abstraction;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables.Dommel;

/// <summary>
/// <see cref="IEntityStore"/> on Dommel. Predicates are passed to Dommel with their captured variables already replaced by
/// constants (<see cref="CapturedValues"/>): Dommel would compile a lambda per variable and per query. A predicate that
/// compares one column with a value (a key, a foreign key) runs SQL built once per type and column.
/// </summary>
public sealed class DommelEntityStore : IEntityStore
{
    private readonly DbConnection _connection;
    private readonly bool _ownsConnection;
    private DbTransaction? _transaction;

    public DommelEntityStore(DbConnection connection, bool ownsConnection = true)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _ownsConnection = ownsConnection;
    }

    /// <summary>
    /// Creates a store from a factory that provides already configured connections,
    /// for example <see cref="DbFactory{TDbConnection}"/>.
    /// </summary>
    public static DommelEntityStore Create(DbProviderFactory factory)
    {
        var connection = factory.CreateConnection()
            ?? throw new InvalidOperationException("The factory could not create a connection.");
        return new DommelEntityStore(connection);
    }

    /// <summary>Creates a store from an ADO.NET factory and a connection string.</summary>
    public static DommelEntityStore Create(DbProviderFactory factory, string connectionString)
    {
        var connection = factory.CreateConnection()
            ?? throw new InvalidOperationException("The factory could not create a connection.");
        connection.ConnectionString = connectionString;
        return new DommelEntityStore(connection);
    }

    /// <summary>Runs a schema creation script (replaces the former constructor code).</summary>
    public static async Task EnsureSchemaAsync(DbProviderFactory factory, string connectionString,
        string createScript, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(createScript)) return;

        await using var connection = factory.CreateConnection()
            ?? throw new InvalidOperationException("The factory could not create a connection.");
        connection.ConnectionString = connectionString;
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = createScript;
        await command.ExecuteNonQueryAsync(ct);
    }

    // ---------- Read ----------

    public async Task<IReadOnlyList<TEntity>> GetAllAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(CancellationToken ct = default) where TEntity : class
    {
        await EnsureOpenAsync(ct);
        var result = await _connection.GetAllAsync<TEntity>(_transaction, ct);
        return result.AsList();
    }

    public async Task<IReadOnlyList<TEntity>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        await EnsureOpenAsync(ct);
        if (await EqualityQueries.SelectAsync(_connection, _transaction, predicate, ct) is { } rows)
            return rows;

        var result = await _connection.SelectAsync(CapturedValues.Inline(predicate), _transaction, ct);
        return result.AsList();
    }

    public async Task<TEntity?> FirstOrDefaultAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        await EnsureOpenAsync(ct);
        var (handled, row) = await EqualityQueries.FirstOrDefaultAsync(_connection, _transaction, predicate, ct);
        return handled ? row : await _connection.FirstOrDefaultAsync(CapturedValues.Inline(predicate), _transaction, ct);
    }

    public async Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize,
        CancellationToken ct = default) where TEntity : class
    {
        Paging.Validate(pageNumber, pageSize);
        await EnsureOpenAsync(ct);
        var result = await _connection.SelectPagedAsync(CapturedValues.Inline(predicate), pageNumber, pageSize, _transaction, ct);
        return result.AsList();
    }

    public async Task<long> CountAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        await EnsureOpenAsync(ct);
        // Dommel's CountAsync takes no cancellation token: cancellation is only possible before the call.
        ct.ThrowIfCancellationRequested();
        return await _connection.CountAsync(CapturedValues.Inline(predicate), _transaction);
    }

    // ---------- Write ----------

    public async Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        await EnsureOpenAsync(ct);
        var id = await _connection.InsertAsync(entity, _transaction, ct);
        AssignGeneratedKey(entity, id);
        SignalIfCommitted();
    }

    /// <exception cref="EntityStoreException">Conflict when the entity has a stale version ([ConcurrencyCheck]).</exception>
    public async Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        await EnsureOpenAsync(ct);

        var updated = ConcurrencyToken.Find(typeof(TEntity)) is { } version
            ? await VersionedCommands.UpdateAsync(_connection, _transaction, entity, version, ct)
            : await _connection.UpdateAsync(entity, _transaction, ct);
        SignalIfCommitted();
        return updated;
    }

    /// <exception cref="EntityStoreException">Conflict when the entity has a stale version ([ConcurrencyCheck]).</exception>
    public async Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        await EnsureOpenAsync(ct);

        var deleted = ConcurrencyToken.Find(typeof(TEntity)) is { } version
            ? await VersionedCommands.DeleteAsync(_connection, _transaction, entity, version, ct)
            : await _connection.DeleteAsync(entity, _transaction, ct);
        SignalIfCommitted();
        return deleted;
    }

    // ---------- Transactions ----------

    public async Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_transaction != null)
            throw new InvalidOperationException("A transaction is already in progress on this store.");

        await EnsureOpenAsync(ct);
        _transaction = await _connection.BeginTransactionAsync(ct);
        return new Transaction(this, _transaction);
    }

    public void Dispose()
    {
        _transaction?.Dispose();
        _transaction = null;
        if (_ownsConnection)
            _connection.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction != null)
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }
        if (_ownsConnection)
            await _connection.DisposeAsync();
    }

    // ---------- Internals ----------

    /// <summary>Outside a transaction, each write is committed when it returns (see <see cref="CommittedWrites"/>).</summary>
    private void SignalIfCommitted()
    {
        if (_transaction is null)
            CommittedWrites.Signal(_connection);
    }

    private async Task EnsureOpenAsync(CancellationToken ct)
    {
        if (_connection.State != ConnectionState.Open)
            await _connection.OpenAsync(ct);
    }

    /// <summary>Writes the database-generated key back into the entity (same behavior as EF).</summary>
    private static void AssignGeneratedKey<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, object? id)
    {
        if (id is null || id is DBNull) return;

        var keys = Resolvers.KeyProperties(typeof(TEntity));
        if (keys.Length != 1 || !keys[0].IsGenerated) return;

        var property = keys[0].Property;
        var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        property.SetValue(entity, Convert.ChangeType(id, targetType));
    }

    private sealed class Transaction : IEntityTransaction
    {
        private readonly DommelEntityStore _store;
        private readonly DbTransaction _inner;

        public Transaction(DommelEntityStore store, DbTransaction inner)
        {
            _store = store;
            _inner = inner;
        }

        public Task<DbResult> CommitAsync(CancellationToken ct = default) => DbResult.RunAsync(async () =>
        {
            await _inner.CommitAsync(ct);
            CommittedWrites.Signal(_store._connection); // the changes are visible now
        });
        public Task<DbResult> RollbackAsync(CancellationToken ct = default) => DbResult.RunAsync(() => _inner.RollbackAsync(ct));

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync(); // rolls back when not committed
            if (ReferenceEquals(_store._transaction, _inner))
                _store._transaction = null;
        }
    }
}

internal static class EnumerableExtensions
{
    public static IReadOnlyList<T> AsList<T>(this IEnumerable<T> source)
        => source as IReadOnlyList<T> ?? source.ToList();
}
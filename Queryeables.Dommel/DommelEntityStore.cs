using Dommel;
using Queryeables.Abstraction;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Queryeables.Dommel;

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

    /// <summary>Crée un store à partir d'une fabrique ADO.NET et d'une chaîne de connexion.</summary>
    public static DommelEntityStore Create(DbProviderFactory factory, string connectionString)
    {
        var connection = factory.CreateConnection()
            ?? throw new InvalidOperationException("La fabrique n'a pas pu créer de connexion.");
        connection.ConnectionString = connectionString;
        return new DommelEntityStore(connection);
    }

    /// <summary>Exécute un script de création de schéma (remplace l'ancien code du constructeur).</summary>
    public static async Task EnsureSchemaAsync(DbProviderFactory factory, string connectionString,
        string createScript, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(createScript)) return;

        await using var connection = factory.CreateConnection()
            ?? throw new InvalidOperationException("La fabrique n'a pas pu créer de connexion.");
        connection.ConnectionString = connectionString;
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = createScript;
        await command.ExecuteNonQueryAsync(ct);
    }

    // ---------- Lecture ----------

    public async Task<IReadOnlyList<TEntity>> SelectAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        await EnsureOpenAsync(ct);
        var result = await _connection.SelectAsync(predicate, _transaction, ct);
        return result.AsList();
    }

    public async Task<TEntity?> FirstOrDefaultAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        await EnsureOpenAsync(ct);
        return await _connection.FirstOrDefaultAsync(predicate, _transaction, ct);
    }

    public async Task<IReadOnlyList<TEntity>> SelectPagedAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize,
        CancellationToken ct = default) where TEntity : class
    {
        Paging.Validate(pageNumber, pageSize);
        await EnsureOpenAsync(ct);
        var result = await _connection.SelectPagedAsync(predicate, pageNumber, pageSize, _transaction, ct);
        return result.AsList();
    }

    // ---------- Écriture ----------

    public async Task InsertAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        await EnsureOpenAsync(ct);
        var id = await _connection.InsertAsync(entity, _transaction, ct);
        AssignGeneratedKey(entity, id);
    }

    public async Task<bool> UpdateAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        await EnsureOpenAsync(ct);
        return await _connection.UpdateAsync(entity, _transaction, ct);
    }

    public async Task<bool> DeleteAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        await EnsureOpenAsync(ct);
        return await _connection.DeleteAsync(entity, _transaction, ct);
    }

    // ---------- Transactions ----------

    public async Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_transaction != null)
            throw new InvalidOperationException("Une transaction est déjà en cours sur ce store.");

        await EnsureOpenAsync(ct);
        _transaction = await _connection.BeginTransactionAsync(ct);
        return new Transaction(this, _transaction);
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

    // ---------- Interne ----------

    private async Task EnsureOpenAsync(CancellationToken ct)
    {
        if (_connection.State != ConnectionState.Open)
            await _connection.OpenAsync(ct);
    }

    /// <summary>Réinjecte la clé générée par la base dans l'entité (comportement identique à EF).</summary>
    private static void AssignGeneratedKey<TEntity>(TEntity entity, object? id)
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

        public Task CommitAsync(CancellationToken ct = default) => _inner.CommitAsync(ct);
        public Task RollbackAsync(CancellationToken ct = default) => _inner.RollbackAsync(ct);

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync(); // annule si non validée
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

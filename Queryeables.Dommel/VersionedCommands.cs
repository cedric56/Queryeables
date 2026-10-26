using Dapper;
using Dommel;
using Queryeables.Abstraction;
using System.Data;
using System.Reflection;

namespace Queryeables.Dommel;

/// <summary>
/// UPDATE and DELETE with a version check, for entities that have a [ConcurrencyCheck] property
/// (Dommel does not handle optimistic concurrency). The SQL is built with Dommel's resolvers and dialect,
/// hence the same table and column names as its own queries.
/// </summary>
internal static class VersionedCommands
{
    private const string ExpectedVersion = "QueryeablesExpectedVersion";

    /// <returns>true if the row was updated; false if it does not exist.</returns>
    /// <exception cref="EntityStoreException">Conflict when the row exists with another version.</exception>
    public static async Task<bool> UpdateAsync<TEntity>(IDbConnection connection, IDbTransaction? transaction,
        TEntity entity, PropertyInfo version, CancellationToken ct) where TEntity : class
    {
        var type = typeof(TEntity);
        var sql = DommelMapper.GetSqlBuilder(connection);
        var keys = Resolvers.KeyProperties(type).Select(k => k.Property).ToArray();
        var keyNames = keys.Select(k => k.Name).ToHashSet();
        var versionColumn = Resolvers.Column(version, sql, false);

        var assignments = Resolvers.Properties(type)
            .Where(p => !p.IsGenerated && !keyNames.Contains(p.Property.Name) && p.Property.Name != version.Name)
            .Select(p => $"{Resolvers.Column(p.Property, sql, false)} = {sql.PrefixParameter(p.Property.Name)}")
            .Append($"{versionColumn} = {versionColumn} + 1");

        var text = $"UPDATE {Resolvers.Table(type, sql)} SET {string.Join(", ", assignments)} " +
                   $"WHERE {KeyCondition(keys, sql)} AND {versionColumn} = {sql.PrefixParameter(ExpectedVersion)}";

        var current = ConcurrencyToken.Get(entity, version);
        var parameters = new DynamicParameters(entity);
        parameters.Add(ExpectedVersion, current);

        var rows = await connection.ExecuteAsync(new CommandDefinition(text, parameters, transaction, cancellationToken: ct));
        if (rows > 0)
        {
            ConcurrencyToken.Set(entity, version, current + 1);
            return true;
        }

        return await ExistsAsync(connection, transaction, entity, keys, sql, ct)
            ? throw ConcurrencyToken.ConflictFor(type)
            : false;
    }

    /// <returns>true if the row was deleted; false if it does not exist.</returns>
    /// <exception cref="EntityStoreException">Conflict when the row exists with another version.</exception>
    public static async Task<bool> DeleteAsync<TEntity>(IDbConnection connection, IDbTransaction? transaction,
        TEntity entity, PropertyInfo version, CancellationToken ct) where TEntity : class
    {
        var type = typeof(TEntity);
        var sql = DommelMapper.GetSqlBuilder(connection);
        var keys = Resolvers.KeyProperties(type).Select(k => k.Property).ToArray();
        var versionColumn = Resolvers.Column(version, sql, false);

        var text = $"DELETE FROM {Resolvers.Table(type, sql)} " +
                   $"WHERE {KeyCondition(keys, sql)} AND {versionColumn} = {sql.PrefixParameter(ExpectedVersion)}";

        var parameters = new DynamicParameters(entity);
        parameters.Add(ExpectedVersion, ConcurrencyToken.Get(entity, version));

        var rows = await connection.ExecuteAsync(new CommandDefinition(text, parameters, transaction, cancellationToken: ct));
        if (rows > 0)
            return true;

        return await ExistsAsync(connection, transaction, entity, keys, sql, ct)
            ? throw ConcurrencyToken.ConflictFor(type)
            : false;
    }

    private static string KeyCondition(PropertyInfo[] keys, ISqlBuilder sql)
    {
        if (keys.Length == 0)
            throw new InvalidOperationException("Optimistic concurrency requires a primary key.");

        return string.Join(" AND ", keys.Select(k => $"{Resolvers.Column(k, sql, false)} = {sql.PrefixParameter(k.Name)}"));
    }

    private static async Task<bool> ExistsAsync<TEntity>(IDbConnection connection, IDbTransaction? transaction,
        TEntity entity, PropertyInfo[] keys, ISqlBuilder sql, CancellationToken ct) where TEntity : class
    {
        var text = $"SELECT COUNT(*) FROM {Resolvers.Table(typeof(TEntity), sql)} WHERE {KeyCondition(keys, sql)}";
        var count = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(text, new DynamicParameters(entity), transaction, cancellationToken: ct));
        return count > 0;
    }
}

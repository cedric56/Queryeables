using Dapper;
using Dommel;
using Queryeables.Abstraction;
using System.Collections.Concurrent;
using System.Data;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Dommel;

/// <summary>
/// Fast path for the queries Queryeables runs most: one column equal to a value (a row by its key for a cache, the
/// children of a parent by their foreign key for a graph). Dommel translates the predicate into SQL on every call; here the
/// SQL is built once per type, column and dialect, with Dommel's own resolvers (same table and column names, same
/// <c>select *</c>), then run by Dapper.
/// </summary>
internal static class EqualityQueries
{
    private const string ValueParameter = "QueryeablesValue";

    private static readonly ConcurrentDictionary<(Type Entity, PropertyInfo Property, Type Connection), string?> Sql = new();

    /// <summary>
    /// Recognizes <c>e => e.Property == value</c> (either side, the property possibly converted), with a non-null value: a
    /// constant or a captured variable, read directly (no copy of the expression tree).
    /// </summary>
    public static bool TryMatch<TEntity>(Expression<Func<TEntity, bool>> predicate, out PropertyInfo property, out object value)
    {
        property = null!;
        value = null!;
        if (predicate.Body is not BinaryExpression { NodeType: ExpressionType.Equal } equal)
            return false;

        var parameter = predicate.Parameters[0];
        if (Member(equal.Left, parameter) is { } left && CapturedValues.TryEvaluate(equal.Right, out var right) && right is not null)
        {
            (property, value) = (left, right);
            return true;
        }
        if (Member(equal.Right, parameter) is { } member && CapturedValues.TryEvaluate(equal.Left, out var leftValue) && leftValue is not null)
        {
            (property, value) = (member, leftValue);
            return true;
        }
        return false;
    }

    /// <summary>The rows matching the predicate, or null when the fast path does not apply.</summary>
    public static async Task<IReadOnlyList<TEntity>?> SelectAsync<TEntity>(IDbConnection connection, IDbTransaction? transaction,
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct) where TEntity : class
    {
        if (!TryMatch(predicate, out var property, out var value) || SqlFor<TEntity>(connection, property) is not { } sql)
            return null;

        var rows = await connection.QueryAsync<TEntity>(new CommandDefinition(sql, Parameters(value), transaction, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// The first row matching the predicate. <c>Handled</c> is false when the fast path does not apply.
    /// </summary>
    public static async Task<(bool Handled, TEntity? Row)> FirstOrDefaultAsync<TEntity>(IDbConnection connection,
        IDbTransaction? transaction, Expression<Func<TEntity, bool>> predicate, CancellationToken ct) where TEntity : class
    {
        if (!TryMatch(predicate, out var property, out var value) || SqlFor<TEntity>(connection, property) is not { } sql)
            return (false, null);

        var row = await connection.QueryFirstOrDefaultAsync<TEntity>(
            new CommandDefinition(sql, Parameters(value), transaction, cancellationToken: ct));
        return (true, row);
    }

    private static DynamicParameters Parameters(object value)
    {
        var parameters = new DynamicParameters();
        parameters.Add(ValueParameter, value);
        return parameters;
    }

    /// <summary>The SQL, or null when the property is not a column of the table (a navigation, [NotMapped]...).</summary>
    private static string? SqlFor<TEntity>(IDbConnection connection, PropertyInfo property)
        => Sql.GetOrAdd((typeof(TEntity), property, connection.GetType()), static (key, connection) =>
        {
            var mapped = Resolvers.Properties(key.Entity).Any(p => p.Property == key.Property)
                         || Resolvers.KeyProperties(key.Entity).Any(k => k.Property == key.Property);
            if (!mapped)
                return null;

            var sql = DommelMapper.GetSqlBuilder(connection);
            return $"select * from {Resolvers.Table(key.Entity, sql)} " +
                   $"where {Resolvers.Column(key.Property, sql, false)} = {sql.PrefixParameter(ValueParameter)}";
        }, connection);

    /// <summary><c>e.Property</c>, possibly converted (a nullable foreign key compared with a key).</summary>
    private static PropertyInfo? Member(Expression expression, ParameterExpression parameter)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            expression = convert.Operand;

        return expression is MemberExpression { Member: PropertyInfo property, Expression: ParameterExpression owner } && owner == parameter
            ? property
            : null;
    }
}

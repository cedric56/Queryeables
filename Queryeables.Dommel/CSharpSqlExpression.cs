using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Dommel;

namespace Queryeables.Dommel;

/// <summary>
/// Dommel's translation of a predicate, made to give the rows C# gives: the result of a filter must not depend on whether it
/// runs in the database, in a cache, or in the offline copy.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>Name.Contains("50%")</c>, <c>StartsWith</c>, <c>EndsWith</c>: <c>%</c>, <c>_</c> and <c>[</c> are text, not
/// wildcards (LIKE ... ESCAPE). With PostgreSQL, LIKE instead of Dommel's ILIKE; with SQLite, <c>instr</c> and
/// <c>substr</c>: both compare case-sensitively, as C# does. SQL Server and MySQL compare as their collation says (case-
/// insensitive by default).</item>
/// <item><c>x != v</c> and <c>!(x == v)</c> on a column that may be null keep its null rows, as C# does
/// (<c>x &lt;&gt; v or x is null</c>).</item>
/// </list>
/// </remarks>
internal sealed class CSharpSqlExpression<TEntity>(ISqlBuilder sqlBuilder) : SqlExpression<TEntity>(sqlBuilder)
{
    private const char Escape = '!';

    protected override object VisitBinary(BinaryExpression expression)
        => expression.NodeType == ExpressionType.NotEqual && NullableComparison(expression) is { } comparison
            ? NotEqualKeepingNulls(comparison.Column, comparison.Value)
            : base.VisitBinary(expression);

    protected override object VisitUnary(UnaryExpression expression)
        => expression.NodeType == ExpressionType.Not && Strip(expression.Operand) is BinaryExpression { NodeType: ExpressionType.Equal } equal
           && NullableComparison(equal) is { } comparison
            ? NotEqualKeepingNulls(comparison.Column, comparison.Value)
            : base.VisitUnary(expression);

    private string NotEqualKeepingNulls(MemberExpression column, object value)
    {
        var name = VisitExpression(column);
        AddParameter(value, out var parameter);
        return $"({name} <> {parameter} or {name} is null)";
    }

    // A column that may be null (a reference type, or Nullable<T>) compared with a value that is not null.
    private static (MemberExpression Column, object Value)? NullableComparison(BinaryExpression expression)
    {
        var (left, right) = (Strip(expression.Left), Strip(expression.Right));
        var (member, constant) = left is MemberExpression { Expression.NodeType: ExpressionType.Parameter } l && right is ConstantExpression r ? (l, r)
            : right is MemberExpression { Expression.NodeType: ExpressionType.Parameter } rm && left is ConstantExpression lc ? (rm, lc)
            : (null, null);
        if (member is null || constant?.Value is not { } value)
            return null;
        var type = member.Type;
        return !type.IsValueType || Nullable.GetUnderlyingType(type) is not null ? (member, value) : null;
    }

    private static Expression Strip(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            expression = convert.Operand;
        return expression;
    }

    protected override object VisitContainsExpression(MethodCallExpression expression, TextSearch textSearch)
    {
        if (expression.Arguments.Count != 1 || VisitExpression(expression.Arguments[0]) is not { } argument)
            return base.VisitContainsExpression(expression, textSearch);
        var text = argument.ToString() ?? string.Empty;
        var column = VisitExpression(expression.Object!);

        if (SqlBuilder is SqliteSqlBuilder)
        {
            // SQLite's LIKE ignores the case of ASCII letters: instr and substr compare exactly.
            AddParameter(text, out var value);
            return textSearch switch
            {
                TextSearch.StartsWith => $"substr({column}, 1, length({value})) = {value}",
                TextSearch.EndsWith => $"(length({column}) >= length({value}) and substr({column}, length({column}) - length({value}) + 1) = {value})",
                _ => $"instr({column}, {value}) > 0",
            };
        }

        var escaped = text
            .Replace(Escape.ToString(), $"{Escape}{Escape}", StringComparison.Ordinal)
            .Replace("%", $"{Escape}%", StringComparison.Ordinal)
            .Replace("_", $"{Escape}_", StringComparison.Ordinal)
            .Replace("[", $"{Escape}[", StringComparison.Ordinal);      // a wildcard of SQL Server
        var pattern = textSearch switch
        {
            TextSearch.StartsWith => $"{escaped}%",
            TextSearch.EndsWith => $"%{escaped}",
            _ => $"%{escaped}%",
        };
        AddParameter(pattern, out var parameter);
        // Dommel writes ILIKE for PostgreSQL: LIKE compares as C# does.
        return SqlBuilder is PostgresSqlBuilder
            ? $"{column} like {parameter} escape '{Escape}'"
            : $"{SqlBuilder.LikeExpression(column.ToString()!, parameter)} escape '{Escape}'";
    }
}

/// <summary>Installs <see cref="CSharpSqlExpression{TEntity}"/> as Dommel's translation, for the entity types of Queryeables.</summary>
internal static class CSharpSql
{
    private static readonly ConcurrentDictionary<Type, Func<ISqlBuilder, object>> Factories = new();
    private static readonly Func<Type, ISqlBuilder, object> Previous;

    static CSharpSql()
    {
        // Dommel's factory is global: the types a Queryeables store reads get the C# translation, the others keep theirs.
        Previous = DommelMapper.SqlExpressionFactory;
        DommelMapper.SqlExpressionFactory = (type, builder) => Factories.TryGetValue(type, out var create) ? create(builder) : Previous(type, builder);
    }

    /// <summary>Called before each translation of <typeparamref name="TEntity"/>: no reflection, so Native AOT is fine.</summary>
    public static void Use<[DynamicallyAccessedMembers(Abstraction.EntityMembers.Required)] TEntity>() where TEntity : class
        => Factories.TryAdd(typeof(TEntity), static builder => new CSharpSqlExpression<TEntity>(builder));
}

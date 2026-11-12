using Microsoft.EntityFrameworkCore;
using System;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.EntityFramework;

/// <summary>
/// <c>StartsWith</c> and <c>EndsWith</c> as C# compares them, on SQLite: EF Core translates them to <c>LIKE</c>, which
/// ignores the case of ASCII letters there (<c>"Ana".StartsWith("an")</c> would be true). They become a comparison of
/// <c>substr</c>, exact as <c>Contains</c> already is (<c>instr</c>), and as on the other stores.
/// </summary>
internal sealed class SqliteText : ExpressionVisitor
{
    private const string Provider = "Microsoft.EntityFrameworkCore.Sqlite";

    private static readonly MethodInfo StartsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo EndsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;
    private static readonly MethodInfo Substring1 = typeof(string).GetMethod(nameof(string.Substring), [typeof(int)])!;
    private static readonly MethodInfo Substring2 = typeof(string).GetMethod(nameof(string.Substring), [typeof(int), typeof(int)])!;
    private static readonly PropertyInfo Length = typeof(string).GetProperty(nameof(string.Length))!;

    private static readonly SqliteText Instance = new();

    /// <summary><paramref name="predicate"/>, made exact for the provider of <paramref name="context"/> when it is SQLite.</summary>
    public static Expression<Func<TEntity, bool>> For<TEntity>(DbContext context, Expression<Func<TEntity, bool>> predicate)
        => context.Database.ProviderName == Provider ? (Expression<Func<TEntity, bool>>)Instance.Visit(predicate) : predicate;

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        if (node.Object is null || (node.Method != StartsWith && node.Method != EndsWith))
            return base.VisitMethodCall(node);

        var text = Visit(node.Object);
        var value = Visit(node.Arguments[0]);
        var textLength = Expression.Property(text, Length);
        var valueLength = Expression.Property(value, Length);
        // length(text) >= length(value) and substr(text, 1, length(value)) = value
        //                                or substr(text, length(text) - length(value) + 1) = value
        var part = node.Method == StartsWith
            ? Expression.Call(text, Substring2, Expression.Constant(0), valueLength)
            : Expression.Call(text, Substring1, Expression.Subtract(textLength, valueLength));
        return Expression.AndAlso(Expression.GreaterThanOrEqual(textLength, valueLength), Expression.Equal(part, value));
    }
}

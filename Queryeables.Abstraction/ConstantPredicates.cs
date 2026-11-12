using System.Linq.Expressions;

namespace Queryeables.Abstraction;

/// <summary>Removes the constant parts of a predicate: <c>e => true</c>, <c>true &amp;&amp; x</c> (a row filter added to "every row").</summary>
public static class ConstantPredicates
{
    /// <summary>The predicate without its constant parts; null when it keeps every row.</summary>
    public static Expression<Func<T, bool>>? Simplify<T>(Expression<Func<T, bool>> predicate, out bool none)
    {
        var body = Fold(predicate.Body);
        none = body is ConstantExpression { Value: false };
        return body is ConstantExpression { Value: true } ? null : Expression.Lambda<Func<T, bool>>(body, predicate.Parameters);
    }

    private static Expression Fold(Expression expression)
    {
        if (expression is not BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } binary)
            return expression is UnaryExpression { NodeType: ExpressionType.Not, Operand: var operand } && Fold(operand) is ConstantExpression { Value: bool b }
                ? Expression.Constant(!b)
                : expression;
        var (left, right) = (Fold(binary.Left), Fold(binary.Right));
        var and = binary.NodeType == ExpressionType.AndAlso;
        return (left, right) switch
        {
            (ConstantExpression { Value: bool l }, _) => l == and ? right : Expression.Constant(l),
            (_, ConstantExpression { Value: bool r }) => r == and ? left : Expression.Constant(r),
            _ => binary.Update(left, binary.Conversion, right),
        };
    }
}

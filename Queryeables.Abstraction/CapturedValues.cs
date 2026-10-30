using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Abstraction;

/// <summary>
/// Replaces the variables a lambda captured (<c>p => p.Id == id</c>) with their current values, read through reflection.
/// <para>
/// Query translators (Dommel, the remote filters) otherwise evaluate each captured variable by compiling a small lambda:
/// a few hundred microseconds and several kilobytes per query, more than the query itself on a fast database.
/// </para>
/// </summary>
public static class CapturedValues
{
    /// <summary>
    /// The same predicate, with constants instead of captured variables. Collections are left as they are: translators
    /// recognize <c>ids.Contains(e.Id)</c> on a variable (Dommel rejects a constant list), and read them only once.
    /// </summary>
    public static Expression<Func<T, bool>> Inline<T>(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return (Expression<Func<T, bool>>)Inliner.Instance.Visit(predicate);
    }

    /// <summary>
    /// Evaluates an expression made of constants and members of constants (a captured variable, a field of an object
    /// held by a variable, a static field or property). False for anything else, for example a method call.
    /// </summary>
    public static bool TryEvaluate(Expression? expression, out object? value)
    {
        switch (expression)
        {
            case ConstantExpression constant:
                value = constant.Value;
                return true;

            case MemberExpression { Member: FieldInfo field } member
                when field.IsStatic || TryEvaluate(member.Expression, out var fieldOwner) && fieldOwner is not null:
                value = field.GetValue(field.IsStatic ? null : Owner(member));
                return true;

            case MemberExpression { Member: PropertyInfo { GetMethod: { } getter } property } member
                when property.GetIndexParameters().Length == 0
                     && (getter.IsStatic || TryEvaluate(member.Expression, out var propertyOwner) && propertyOwner is not null):
                value = property.GetValue(getter.IsStatic ? null : Owner(member));
                return true;

            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
                when convert.Type.IsAssignableFrom(convert.Operand.Type) && TryEvaluate(convert.Operand, out var operand):
                value = operand; // a boxing or an upcast: the value does not change
                return true;

            default:
                value = null;
                return false;
        }

        static object? Owner(MemberExpression member) => TryEvaluate(member.Expression, out var owner) ? owner : null;
    }

    private sealed class Inliner : ExpressionVisitor
    {
        public static readonly Inliner Instance = new();

        protected override Expression VisitMember(MemberExpression node)
            => node.Expression is not ParameterExpression && !IsCollection(node.Type) && TryEvaluate(node, out var value)
                ? Expression.Constant(value, node.Type)
                : base.VisitMember(node);

        private static bool IsCollection(Type type) => type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);
    }
}

using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Security.Claims;

namespace Queryeables.Remote;

/// <summary>
/// The rows of <typeparamref name="T"/> a user may reach, decided by the server: added to every read, and checked on every
/// write. A row outside the filter does not exist for that user: reads skip it, and an update or a delete of it answers
/// NotFound. Multi-user and multi-tenant applications need one on every exposed type that holds per-user data.
/// </summary>
/// <example>
/// <code>
/// server.Expose&lt;Order&gt;(EntityAccessPolicies.Authenticated,
///     rows: EntityRowFilter.For&lt;Order, long&gt;(o => o.Id, user => o => o.TenantId == user.TenantId()));
/// </code>
/// </example>
public sealed class EntityRowFilter<[DynamicallyAccessedMembers(EntityMembers.Required)] T> where T : class
{
    private readonly Func<ClaimsPrincipal?, Expression<Func<T, bool>>> _rows;
    private readonly Func<T, Expression<Func<T, bool>>> _sameKey;

    internal EntityRowFilter(Func<ClaimsPrincipal?, Expression<Func<T, bool>>> rows, Func<T, Expression<Func<T, bool>>> sameKey)
    {
        _rows = rows;
        _sameKey = sameKey;
    }

    /// <summary>The rows the user may reach. Anonymous calls get a null user: return <c>_ => false</c> to refuse them.</summary>
    internal Expression<Func<T, bool>> Rows(ClaimsPrincipal? user)
        => _rows(user) ?? throw new InvalidOperationException($"The row filter of {typeof(T).Name} returned no predicate.");

    /// <summary><c>e => e.Key == entity.Key</c>: the stored row a write targets.</summary>
    internal Expression<Func<T, bool>> SameKey(T entity) => _sameKey(entity);

    /// <summary><paramref name="predicate"/> and the rows of the user.</summary>
    internal Expression<Func<T, bool>> Restrict(Expression<Func<T, bool>> predicate, ClaimsPrincipal? user)
        => And(predicate, Rows(user));

    internal static Expression<Func<T, bool>> And(Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
    {
        var parameter = left.Parameters[0];
        var rightBody = new Replacer(right.Parameters[0], parameter).Visit(right.Body)!;
        return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(left.Body, rightBody), parameter);
    }

    private sealed class Replacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}

public static class EntityRowFilter
{
    /// <summary>A row filter for <typeparamref name="T"/>.</summary>
    /// <param name="key">The key, to find the stored row an update or a delete targets.</param>
    /// <param name="rows">The rows the user may reach, for example <c>user => o => o.OwnerId == user.FindFirstValue("sub")</c>.
    /// Anything a remote filter can express: comparisons, <c>list.Contains(e.Prop)</c>, <c>&amp;&amp;</c>, <c>||</c>...</param>
    public static EntityRowFilter<T> For<[DynamicallyAccessedMembers(EntityMembers.Required)] T, TKey>(
        Expression<Func<T, TKey>> key, Func<ClaimsPrincipal?, Expression<Func<T, bool>>> rows)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(rows);
        var keyOf = key.Compile();
        return new EntityRowFilter<T>(rows, entity => Expression.Lambda<Func<T, bool>>(
            Expression.Equal(key.Body, Expression.Constant(keyOf(entity), typeof(TKey))), key.Parameters));
    }
}

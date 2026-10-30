using Queryeables.Abstraction;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Remote;

/// <summary>
/// Conversion between LINQ predicates and conditions that any transport can carry.
/// Client: predicate → conditions. Server: conditions → predicate, over a whitelist of operations.
/// </summary>
internal static class Filters
{
    private static readonly MethodInfo StringStartsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo StringEndsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;
    private static readonly MethodInfo StringContains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;

    // =====================================================================
    // Client: predicate → conditions
    // =====================================================================

    /// <summary>
    /// Translates a predicate into conditions. Accepted terms:
    /// <list type="bullet">
    /// <item>comparisons between a property and a value: ==, !=, &gt;, &gt;=, &lt;, &lt;=;</item>
    /// <item>boolean property: <c>e.Active</c>, <c>!e.Active</c>;</item>
    /// <item>membership in a list: <c>ids.Contains(e.Id)</c>;</item>
    /// <item>text: <c>e.Name.StartsWith(x)</c>, <c>EndsWith</c>, <c>Contains</c>;</item>
    /// <item><c>e => true</c>.</item>
    /// </list>
    /// Terms combine with <c>&amp;&amp;</c>, <c>||</c>, parentheses and <c>!</c> (pushed down to the terms: <c>!(a &amp;&amp; b)</c>
    /// becomes <c>!a || !b</c>). A predicate that only uses <c>&amp;&amp;</c>, or only <c>||</c>, is sent as a flat list with
    /// <c>Any</c>; any other is sent in postfix order, with <see cref="FilterOperator.And"/> and <see cref="FilterOperator.Or"/>
    /// nodes. Anything else throws <see cref="NotSupportedException"/>, before any server call.
    /// </summary>
    public static (FilterCondition[] Conditions, bool Any) FromPredicate<T>(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var parameter = predicate.Parameters[0];

        if (predicate.Body is ConstantExpression { Value: true })
            return ([], false);

        var output = new List<FilterCondition>();
        Emit(predicate.Body, parameter, negate: false, output);

        // Flat form when it suffices: what every server understands.
        var logical = output.Where(IsLogical).ToList();
        if (logical.Count == 0)
            return (output.ToArray(), false);
        if (logical.Count == 1 && IsLogical(output[^1]))
            return (output.Take(output.Count - 1).ToArray(), output[^1].Operator == FilterOperator.Or);

        if (output.Count > MaxValues)
            throw Unsupported($"more than {MaxValues} conditions");
        return (output.ToArray(), false);
    }

    private static bool IsLogical(FilterCondition condition) => condition.Operator is FilterOperator.And or FilterOperator.Or;

    /// <summary>Writes the expression in postfix order; <paramref name="negate"/> pushes a <c>!</c> down to the terms.</summary>
    private static void Emit(Expression expression, ParameterExpression parameter, bool negate, List<FilterCondition> output)
    {
        if (expression.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
        {
            var terms = new List<Expression>();
            Flatten(expression, expression.NodeType, terms);
            foreach (var term in terms)
                Emit(term, parameter, negate, output);

            // De Morgan: !(a && b) == !a || !b.
            var isOr = expression.NodeType == ExpressionType.OrElse ^ negate;
            output.Add(new FilterCondition(string.Empty, isOr ? FilterOperator.Or : FilterOperator.And, terms.Count));
            return;
        }

        // !(...) that is not a negated boolean property: push it down.
        if (expression is UnaryExpression { NodeType: ExpressionType.Not } not
            && !(AsProperty(not.Operand, parameter) is { } flag && IsBoolean(flag)))
        {
            Emit(not.Operand, parameter, !negate, output);
            return;
        }

        var condition = ParseTerm(expression, parameter);
        output.Add(negate ? Negate(condition, expression, parameter) : condition);
    }

    /// <summary>Collects the operands of a chain of the same operator: a &amp;&amp; b &amp;&amp; c gives a, b, c.</summary>
    private static void Flatten(Expression expression, ExpressionType type, List<Expression> terms)
    {
        if (expression.NodeType == type)
        {
            var binary = (BinaryExpression)expression;
            Flatten(binary.Left, type, terms);
            Flatten(binary.Right, type, terms);
        }
        else
        {
            terms.Add(expression);
        }
    }

    /// <summary>The opposite condition, so that no NOT has to be translated by the database.</summary>
    private static FilterCondition Negate(FilterCondition condition, Expression term, ParameterExpression parameter)
    {
        // With a null, every ordering is false in C#: !(e.Score > 1) is true for a null score, e.Score <= 1 is not.
        if (condition.Operator is FilterOperator.GreaterThan or FilterOperator.GreaterThanOrEqual
                or FilterOperator.LessThan or FilterOperator.LessThanOrEqual
            && term is BinaryExpression binary
            && (AsProperty(binary.Left, parameter) ?? AsProperty(binary.Right, parameter)) is { } compared
            && Nullable.GetUnderlyingType(compared.PropertyType) is not null)
            throw Unsupported($"'{term}' on a nullable property cannot be negated");

        var op = condition.Operator switch
        {
            FilterOperator.Equal when condition.Value is bool flag => (FilterOperator?)null,
            FilterOperator.Equal => FilterOperator.NotEqual,
            FilterOperator.NotEqual => FilterOperator.Equal,
            FilterOperator.GreaterThan => FilterOperator.LessThanOrEqual,
            FilterOperator.GreaterThanOrEqual => FilterOperator.LessThan,
            FilterOperator.LessThan => FilterOperator.GreaterThanOrEqual,
            FilterOperator.LessThanOrEqual => FilterOperator.GreaterThan,
            _ => throw Unsupported($"'{term}' cannot be negated")
        };

        // A boolean flag: !e.Active is e.Active == false (and keeps excluding nulls, like the database would).
        return op is null
            ? new FilterCondition(condition.Property, FilterOperator.Equal, !(bool)condition.Value!)
            : new FilterCondition(condition.Property, op.Value, condition.Value);
    }

    private static FilterCondition ParseTerm(Expression term, ParameterExpression parameter)
    {
        switch (term)
        {
            case BinaryExpression binary when ComparisonOperator(binary.NodeType) is { } op:
                if (AsProperty(binary.Left, parameter) is { } left && !UsesParameter(binary.Right, parameter))
                    return new FilterCondition(left.Name, op, Normalize(Evaluate(binary.Right)));
                if (AsProperty(binary.Right, parameter) is { } right && !UsesParameter(binary.Left, parameter))
                    return new FilterCondition(right.Name, Mirror(op), Normalize(Evaluate(binary.Left)));
                break;

            case UnaryExpression { NodeType: ExpressionType.Not } negation
                when AsProperty(negation.Operand, parameter) is { } negated && IsBoolean(negated):
                return new FilterCondition(negated.Name, FilterOperator.Equal, false);

            case MethodCallExpression call when ParseMethodCall(call, parameter) is { } condition:
                return condition;

            default:
                if (AsProperty(term, parameter) is { } flag && IsBoolean(flag))
                    return new FilterCondition(flag.Name, FilterOperator.Equal, true);
                break;
        }

        throw Unsupported($"'{term}' is not a supported condition");
    }

    private static FilterCondition? ParseMethodCall(MethodCallExpression call, ParameterExpression parameter)
    {
        // e.Name.StartsWith(x), EndsWith(x), Contains(x)
        if (call.Method.DeclaringType == typeof(string) && !call.Method.IsStatic && call.Arguments.Count == 1
            && AsProperty(call.Object!, parameter) is { } text && !UsesParameter(call.Arguments[0], parameter))
        {
            FilterOperator? op = call.Method.Name switch
            {
                nameof(string.StartsWith) => FilterOperator.StartsWith,
                nameof(string.EndsWith) => FilterOperator.EndsWith,
                nameof(string.Contains) => FilterOperator.Contains,
                _ => null
            };
            return op is null ? null : new FilterCondition(text.Name, op.Value, Normalize(Evaluate(call.Arguments[0])));
        }

        if (call.Method.Name != "Contains")
            return null;

        // list.Contains(e.Id): Enumerable.Contains(list, e.Id), MemoryExtensions.Contains(span, e.Id)
        // (form produced by C# 14 for an array) or an instance method of a collection.
        var (source, item) = call.Object is null && call.Arguments.Count == 2
            ? (call.Arguments[0], call.Arguments[1])
            : (call.Object, call.Arguments.Count == 1 ? call.Arguments[0] : null);

        // "abc".Contains(e.Name) is not a list of values: a string is not a collection here.
        if (source is null || item is null || source.Type == typeof(string)
            || AsProperty(item, parameter) is not { } member || UsesParameter(source, parameter))
            return null;

        var values = Evaluate(UnwrapSpan(source)) as IEnumerable
            ?? throw Unsupported($"'{source}' is not a collection");
        return new FilterCondition(member.Name, FilterOperator.In, values.Cast<object?>().Select(Normalize).ToArray());
    }

    /// <summary>An array converted to a ReadOnlySpan cannot be evaluated: evaluate the original array instead.</summary>
    private static Expression UnwrapSpan(Expression source)
    {
        while (true)
        {
            switch (source)
            {
                case MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } implicitCall:
                    source = implicitCall.Arguments[0];
                    continue;
                case UnaryExpression { NodeType: ExpressionType.Convert } convert when convert.Type.IsByRefLike:
                    source = convert.Operand;
                    continue;
                default:
                    return source;
            }
        }
    }

    private static FilterOperator? ComparisonOperator(ExpressionType type) => type switch
    {
        ExpressionType.Equal => FilterOperator.Equal,
        ExpressionType.NotEqual => FilterOperator.NotEqual,
        ExpressionType.GreaterThan => FilterOperator.GreaterThan,
        ExpressionType.GreaterThanOrEqual => FilterOperator.GreaterThanOrEqual,
        ExpressionType.LessThan => FilterOperator.LessThan,
        ExpressionType.LessThanOrEqual => FilterOperator.LessThanOrEqual,
        _ => null
    };

    /// <summary><c>5 &lt; e.Id</c> becomes <c>e.Id &gt; 5</c>.</summary>
    private static FilterOperator Mirror(FilterOperator op) => op switch
    {
        FilterOperator.GreaterThan => FilterOperator.LessThan,
        FilterOperator.GreaterThanOrEqual => FilterOperator.LessThanOrEqual,
        FilterOperator.LessThan => FilterOperator.GreaterThan,
        FilterOperator.LessThanOrEqual => FilterOperator.GreaterThanOrEqual,
        _ => op
    };

    /// <summary>Returns the property when the expression is <c>e.Property</c> (possibly converted).</summary>
    private static PropertyInfo? AsProperty(Expression expression, ParameterExpression parameter)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            expression = convert.Operand;

        return expression is MemberExpression { Member: PropertyInfo property, Expression: ParameterExpression p } && p == parameter
            ? property
            : null;
    }

    private static bool IsBoolean(PropertyInfo property)
        => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(bool);

    private static bool UsesParameter(Expression expression, ParameterExpression parameter)
    {
        var finder = new ParameterFinder(parameter);
        finder.Visit(expression);
        return finder.Found;
    }

    /// <summary>The value of an expression that does not use the parameter: read directly when it is a captured variable.</summary>
    private static object? Evaluate(Expression expression)
        => CapturedValues.TryEvaluate(expression, out var value)
            ? value
            : Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object))).Compile()();

    /// <summary>Reduces the value to a primitive type that any serializer carries (no custom type to register).</summary>
    private static object? Normalize(object? value) => value switch
    {
        null => null,
        Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        char c => c.ToString(),
        _ => value
    };

    private static NotSupportedException Unsupported(string reason)
        => new($"Predicate cannot be sent to the server: {reason}. Supported: comparisons between a " +
               "property and a value, boolean properties, list.Contains(e.Prop), StartsWith/EndsWith/Contains " +
               "on strings, combined with &&, || and !.");

    private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter) Found = true;
            return node;
        }
    }

    // =====================================================================
    // Server: conditions → predicate
    // =====================================================================

    /// <summary>
    /// Most values accepted by the In operator, and most conditions in a filter. Each value becomes a node of the
    /// expression tree and a SQL parameter: without a limit, one request could exhaust the stack of the server or exceed
    /// the parameter limit of the database (2100 on SQL Server).
    /// </summary>
    public const int MaxValues = 1000;

    /// <summary>Builds the predicate, rejecting any condition outside the whitelist.</summary>
    public static Expression<Func<T, bool>> ToPredicate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        IReadOnlyList<FilterCondition> conditions, bool any)
    {
        if (conditions.Count > MaxValues)
            throw new ArgumentException($"A filter is limited to {MaxValues} conditions.");

        var parameter = Expression.Parameter(typeof(T), "e");
        if (conditions.Any(c => c is { Operator: FilterOperator.And or FilterOperator.Or }))
            return Expression.Lambda<Func<T, bool>>(FromPostfix<T>(conditions, parameter), parameter);

        Expression? body = null;

        foreach (var condition in conditions)
        {
            ArgumentNullException.ThrowIfNull(condition);
            var property = typeof(T).GetProperty(condition.Property, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Unknown property '{condition.Property}' on {typeof(T).Name}.");

            var term = Build(Expression.Property(parameter, property), condition);
            body = body is null ? term
                : any ? Expression.OrElse(body, term)
                : Expression.AndAlso(body, term);
        }

        body ??= Expression.Constant(true);
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    /// <summary>Most nested levels of AND and OR: the expression tree and the SQL stay small.</summary>
    private const int MaxDepth = 32;

    /// <summary>Rebuilds a predicate written in postfix order (see <see cref="FromPredicate{T}"/>).</summary>
    private static Expression FromPostfix<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        IReadOnlyList<FilterCondition> conditions, ParameterExpression parameter)
    {
        var stack = new Stack<(Expression Expression, int Depth)>();
        foreach (var condition in conditions)
        {
            ArgumentNullException.ThrowIfNull(condition);
            if (condition.Operator is not (FilterOperator.And or FilterOperator.Or))
            {
                stack.Push((Term<T>(condition, parameter), 0));
                continue;
            }

            int count;
            try
            {
                count = Convert.ToInt32(condition.Value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                throw new ArgumentException($"{condition.Operator} expects its number of operands.", ex);
            }
            if (count < 2 || count > stack.Count)
                throw new ArgumentException($"{condition.Operator} expects between 2 and {stack.Count} operands, not {count}.");

            var operands = new (Expression Expression, int Depth)[count];
            for (var i = count - 1; i >= 0; i--)
                operands[i] = stack.Pop();

            var depth = operands.Max(o => o.Depth) + 1;
            if (depth > MaxDepth)
                throw new ArgumentException($"A filter is limited to {MaxDepth} nested levels.");

            var combined = operands[0].Expression;
            for (var i = 1; i < count; i++)
            {
                combined = condition.Operator == FilterOperator.Or
                    ? Expression.OrElse(combined, operands[i].Expression)
                    : Expression.AndAlso(combined, operands[i].Expression);
            }
            stack.Push((combined, depth));
        }

        if (stack.Count != 1)
            throw new ArgumentException("The filter does not combine into a single condition.");
        return stack.Pop().Expression;
    }

    private static Expression Term<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        FilterCondition condition, ParameterExpression parameter)
    {
        var property = typeof(T).GetProperty(condition.Property, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException($"Unknown property '{condition.Property}' on {typeof(T).Name}.");
        return Build(Expression.Property(parameter, property), condition);
    }

    private static Expression Build(MemberExpression member, FilterCondition condition)
    {
        var type = member.Type;
        var name = condition.Property;

        switch (condition.Operator)
        {
            case FilterOperator.Equal or FilterOperator.NotEqual
                or FilterOperator.GreaterThan or FilterOperator.GreaterThanOrEqual
                or FilterOperator.LessThan or FilterOperator.LessThanOrEqual:
                if (condition.Value is IList and not byte[])
                    throw new ArgumentException($"'{name}': a list is only accepted with the In operator.");
                return Compare(member, Constant(condition.Value, type, name), condition.Operator, name);

            case FilterOperator.In:
                if (condition.Value is not IList values || condition.Value is byte[])
                    throw new ArgumentException($"'{name}': the In operator expects a list of values.");
                if (values.Count == 0)
                    return Expression.Constant(false); // no value: no row
                if (values.Count > MaxValues)
                    throw new ArgumentException($"'{name}': the In operator is limited to {MaxValues} values.");
                Expression? anyOf = null;
                foreach (var value in values)
                {
                    var equal = Compare(member, Constant(value, type, name), FilterOperator.Equal, name);
                    anyOf = anyOf is null ? equal : Expression.OrElse(anyOf, equal);
                }
                return anyOf!;

            case FilterOperator.StartsWith or FilterOperator.EndsWith or FilterOperator.Contains:
                if (type != typeof(string))
                    throw new ArgumentException($"'{name}' is not a string: {condition.Operator} is not possible.");
                if (condition.Value is not string text)
                    throw new ArgumentException($"'{name}': {condition.Operator} expects a string.");
                var method = condition.Operator switch
                {
                    FilterOperator.StartsWith => StringStartsWith,
                    FilterOperator.EndsWith => StringEndsWith,
                    _ => StringContains
                };
                return Expression.Call(member, method, Expression.Constant(text));

            default:
                throw new ArgumentException($"Unknown operator: {condition.Operator}.");
        }
    }

    private static ConstantExpression Constant(object? value, Type type, string name)
        => Expression.Constant(ConvertTo(value, type, name), type);

    /// <summary>Comparison, including for enums, which expression trees do not compare directly.</summary>
    private static Expression Compare(Expression left, Expression right, FilterOperator op, string name)
    {
        var underlying = Nullable.GetUnderlyingType(left.Type);
        var core = underlying ?? left.Type;
        if (core.IsEnum)
        {
            var numeric = Enum.GetUnderlyingType(core);
            var target = underlying is null ? numeric : NullableOf(numeric);
            left = Expression.Convert(left, target);
            right = Expression.Convert(right, target);
        }

        try
        {
            return op switch
            {
                FilterOperator.Equal => Expression.Equal(left, right),
                FilterOperator.NotEqual => Expression.NotEqual(left, right),
                FilterOperator.GreaterThan => Expression.GreaterThan(left, right),
                FilterOperator.GreaterThanOrEqual => Expression.GreaterThanOrEqual(left, right),
                FilterOperator.LessThan => Expression.LessThan(left, right),
                FilterOperator.LessThanOrEqual => Expression.LessThanOrEqual(left, right),
                _ => throw new ArgumentException($"Unexpected operator: {op}.")
            };
        }
        catch (InvalidOperationException ex)
        {
            // For example "Name > 'A'": no comparison operator on strings.
            throw new ArgumentException($"'{name}' ({left.Type.Name}) cannot be compared with {op}.", ex);
        }
    }

    /// <summary>
    /// Nullable form of an enum's underlying type. A fixed table rather than MakeGenericType, which requires
    /// runtime code generation (not available with Native AOT).
    /// </summary>
    private static Type NullableOf(Type numeric) => Type.GetTypeCode(numeric) switch
    {
        TypeCode.Int32 => typeof(int?),
        TypeCode.Int64 => typeof(long?),
        TypeCode.Int16 => typeof(short?),
        TypeCode.Byte => typeof(byte?),
        TypeCode.SByte => typeof(sbyte?),
        TypeCode.UInt32 => typeof(uint?),
        TypeCode.UInt64 => typeof(ulong?),
        TypeCode.UInt16 => typeof(ushort?),
        _ => throw new NotSupportedException($"Unexpected enum underlying type: {numeric.Name}.")
    };

    /// <summary>
    /// Converts a received value to the property type. A serializer may not keep the exact type
    /// of a number stored in an object (a 5 comes back as a byte): without conversion, Expression.Constant fails.
    /// </summary>
    public static object? ConvertTo(object? value, Type type, string name)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (value is null)
        {
            if (type.IsValueType && target == type)
                throw new ArgumentException($"Property '{name}' ({type.Name}) does not accept null.");
            return null;
        }

        if (target.IsInstanceOfType(value))
            return value;

        var invariant = CultureInfo.InvariantCulture;
        try
        {
            if (target.IsEnum)
                return value is string s
                    ? Enum.Parse(target, s, ignoreCase: true)
                    : Enum.ToObject(target, Convert.ToInt64(value, invariant));

            if (target == typeof(Guid))
                return value is byte[] bytes ? new Guid(bytes) : Guid.Parse(Convert.ToString(value, invariant)!);

            if (target == typeof(DateTimeOffset))
                return value is DateTime dt
                    ? new DateTimeOffset(dt)
                    : DateTimeOffset.Parse(Convert.ToString(value, invariant)!, invariant, DateTimeStyles.RoundtripKind);

            if (target == typeof(DateTime) && value is string date)
                return DateTime.Parse(date, invariant, DateTimeStyles.RoundtripKind);

            if (target == typeof(TimeSpan))
                return TimeSpan.Parse(Convert.ToString(value, invariant)!, invariant);

            return Convert.ChangeType(value, target, invariant);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new ArgumentException(
                $"Invalid value '{value}' ({value.GetType().Name}) for property '{name}' ({type.Name}).", ex);
        }
    }
}

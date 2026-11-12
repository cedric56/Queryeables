using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;

namespace Queryeables.Abstraction;

/// <summary>What an aggregate computes over the matching rows.</summary>
public enum AggregateFunction
{
    /// <summary>The number of rows (a value is not needed).</summary>
    Count,

    /// <summary>The sum: a decimal for decimals, a double for floating point numbers, a long for integers.</summary>
    Sum,

    /// <summary>The smallest value, of the type of the value; null without rows.</summary>
    Min,

    /// <summary>The largest value, of the type of the value; null without rows.</summary>
    Max,

    /// <summary>The average: a decimal for decimals, a double otherwise; null without rows.</summary>
    Average,
}

/// <summary>One value an aggregation computes: a function, and the value of each row it applies to.</summary>
/// <param name="ValueType">The type of the values, when <paramref name="Value"/> returns them boxed (object).</param>
public sealed record AggregateValue(AggregateFunction Function, LambdaExpression? Value, Type? ValueType = null)
{
    /// <summary>The type of the values the function applies to.</summary>
    public Type? Type => ValueType ?? Value?.ReturnType;
}

/// <summary>
/// What <see cref="IEntityStore.AggregateAsync{TEntity}"/> computes: values over the matching rows, by group or for all of
/// them. Totals of a dashboard, the number of orders per status, the last date of an order...
/// </summary>
/// <example>
/// <code>
/// var perStatus = await store.AggregateAsync(o => o.Year == 2026,
///     Aggregation.Of&lt;Order&gt;().GroupBy(o => o.Status).Count().Sum(o => o.Total));
/// </code>
/// </example>
public sealed class Aggregation<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity> where TEntity : class
{
    private readonly List<AggregateValue> _values = [];

    /// <summary>The key of the groups; null for one row over every matching row.</summary>
    public LambdaExpression? Key { get; private set; }

    /// <summary>The type of the key (the value before its boxing, for a key returned as object); null without a group.</summary>
    public Type? KeyType => Key is null ? null
        : Key.Body is UnaryExpression { NodeType: ExpressionType.Convert } box && Key.ReturnType == typeof(object) ? box.Operand.Type : Key.ReturnType;

    /// <summary>
    /// <paramref name="value"/>, as a database returned it, in <paramref name="type"/>: a number of another width, a flag
    /// stored as a number, a date stored as text...
    /// </summary>
    public static object? Coerce(object? value, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (value is null or DBNull)
            return null;
        var core = Nullable.GetUnderlyingType(type) ?? type;
        if (core.IsInstanceOfType(value) || core == typeof(object))
            return value;
        var invariant = CultureInfo.InvariantCulture;
        if (core.IsEnum)
            return value is string name ? Enum.Parse(core, name, ignoreCase: true) : Enum.ToObject(core, Convert.ToInt64(value, invariant));
        if (core == typeof(Guid))
            return value is byte[] bytes ? new Guid(bytes) : Guid.Parse(Convert.ToString(value, invariant)!);
        if (core == typeof(DateTime) && value is string date)
            return DateTime.Parse(date, invariant, DateTimeStyles.RoundtripKind);
        if (core == typeof(DateTimeOffset))
            return value is DateTime moment ? new DateTimeOffset(moment) : DateTimeOffset.Parse(Convert.ToString(value, invariant)!, invariant);
        if (core == typeof(DateOnly))
            return value is DateTime day ? DateOnly.FromDateTime(day) : DateOnly.Parse(Convert.ToString(value, invariant)!, invariant);
        if (core == typeof(TimeSpan))
            return value is string span ? TimeSpan.Parse(span, invariant) : TimeSpan.FromTicks(Convert.ToInt64(value, invariant));
        return Convert.ChangeType(value, core, invariant);
    }

    /// <summary>The values, in the order of <see cref="AggregateRow.Values"/>.</summary>
    public IReadOnlyList<AggregateValue> Values => _values;

    /// <summary>One result row per distinct value of <paramref name="key"/> (a column, or a value computed from columns).</summary>
    public Aggregation<TEntity> GroupBy<TKey>(Expression<Func<TEntity, TKey>> key)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        return this;
    }

    public Aggregation<TEntity> Count() => Add(AggregateFunction.Count, null);

    public Aggregation<TEntity> Sum<TValue>(Expression<Func<TEntity, TValue>> value) => Add(AggregateFunction.Sum, value);

    public Aggregation<TEntity> Min<TValue>(Expression<Func<TEntity, TValue>> value) => Add(AggregateFunction.Min, value);

    public Aggregation<TEntity> Max<TValue>(Expression<Func<TEntity, TValue>> value) => Add(AggregateFunction.Max, value);

    public Aggregation<TEntity> Average<TValue>(Expression<Func<TEntity, TValue>> value) => Add(AggregateFunction.Average, value);

    /// <summary>Adds a value; <paramref name="value"/> is null for <see cref="AggregateFunction.Count"/> only.</summary>
    /// <param name="valueType">The type of the values when <paramref name="value"/> returns them boxed (object).</param>
    public Aggregation<TEntity> Add(AggregateFunction function, LambdaExpression? value, Type? valueType = null)
    {
        if ((function == AggregateFunction.Count) != (value is null))
            throw new ArgumentException(function == AggregateFunction.Count ? "Count takes no value." : $"{function} needs a value.", nameof(value));
        if (value is not null && (value.Parameters.Count != 1 || value.Parameters[0].Type != typeof(TEntity)))
            throw new ArgumentException($"The value must be a function of {typeof(TEntity).Name}.", nameof(value));
        _values.Add(new AggregateValue(function, value, valueType));
        return this;
    }

    /// <summary>
    /// Computes the aggregation over <paramref name="values"/>: one array per row, the key first when grouped, then the value
    /// of each <see cref="Values"/> entry that has one. What a store does after reading only these values.
    /// </summary>
    public IReadOnlyList<AggregateRow> Compute(IEnumerable<object?[]> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var offset = Key is null ? 0 : 1;
        var groups = new Dictionary<GroupKey, List<object?[]>>();
        var order = new List<GroupKey>();
        foreach (var row in values)
        {
            var key = new GroupKey(Key is null ? null : row[0]);
            if (!groups.TryGetValue(key, out var rows))
            {
                groups[key] = rows = [];
                order.Add(key);
            }
            rows.Add(row);
        }
        if (Key is null && groups.Count == 0)
        {
            groups[new GroupKey(null)] = [];
            order.Add(new GroupKey(null));
        }

        var result = new List<AggregateRow>(groups.Count);
        foreach (var key in order)
        {
            var rows = groups[key];
            var computed = new object?[_values.Count];
            var column = offset;
            for (var i = 0; i < _values.Count; i++)
            {
                var value = _values[i];
                if (value.Function == AggregateFunction.Count)
                {
                    computed[i] = (long)rows.Count;
                    continue;
                }
                var index = column++;
                computed[i] = Apply(value.Function, value.Type!, rows.Select(r => r[index]));
            }
            result.Add(new AggregateRow(key.Value, rows.Count, computed));
        }
        return result;
    }

    /// <summary>
    /// <c>e =&gt; new object?[] { key, value1, ... }</c>: the values <see cref="Compute"/> needs, read by a store in one query
    /// (only these columns, when it projects). Null when nothing but a count is asked, without a group.
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "An array of object: its type exists in every application.")]
    public Expression<Func<TEntity, object?[]>>? Projection()
    {
        var expressions = (Key is null ? ValueExpressions : ValueExpressions.Prepend(Key)).ToList();
        if (expressions.Count == 0)
            return null;
        var parameter = Expression.Parameter(typeof(TEntity), "e");
        var items = expressions.Select(e => (Expression)Expression.Convert(
            new ParameterReplacer(e.Parameters[0], parameter).Visit(e.Body), typeof(object)));
        return Expression.Lambda<Func<TEntity, object?[]>>(Expression.NewArrayInit(typeof(object), items), parameter);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }

    /// <summary>The functions that read a value, in order: one per value of a row given to <see cref="Compute"/>.</summary>
    public IEnumerable<LambdaExpression> ValueExpressions => _values.Where(v => v.Value is not null).Select(v => v.Value!);

    /// <summary>The type of the result of <paramref name="function"/> over values of <paramref name="type"/>.</summary>
    public static Type ResultType(AggregateFunction function, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var core = Nullable.GetUnderlyingType(type) ?? type;
        return function switch
        {
            AggregateFunction.Count => typeof(long),
            AggregateFunction.Sum => core == typeof(decimal) ? typeof(decimal) : IsFloating(core) ? typeof(double) : typeof(long),
            AggregateFunction.Average => core == typeof(decimal) ? typeof(decimal) : typeof(double),
            _ => core,
        };
    }

    private static bool IsFloating(Type type) => type == typeof(double) || type == typeof(float);

    private static object? Apply(AggregateFunction function, Type type, IEnumerable<object?> source)
    {
        var values = source.Where(v => v is not null).ToList();
        var core = Nullable.GetUnderlyingType(type) ?? type;
        var invariant = CultureInfo.InvariantCulture;
        switch (function)
        {
            case AggregateFunction.Sum when core == typeof(decimal):
                return values.Sum(v => Convert.ToDecimal(v, invariant));
            case AggregateFunction.Sum when IsFloating(core):
                return values.Sum(v => Convert.ToDouble(v, invariant));
            case AggregateFunction.Sum:
                return values.Sum(v => Convert.ToInt64(v, invariant));
            case AggregateFunction.Average when values.Count == 0:
                return null;
            case AggregateFunction.Average when core == typeof(decimal):
                return values.Average(v => Convert.ToDecimal(v, invariant));
            case AggregateFunction.Average:
                return values.Average(v => Convert.ToDouble(v, invariant));
            case AggregateFunction.Min:
                return values.Count == 0 ? null : values.Min(Comparer);
            case AggregateFunction.Max:
                return values.Count == 0 ? null : values.Max(Comparer);
            default:
                throw new NotSupportedException($"Unknown aggregate: {function}.");
        }
    }

    private static readonly IComparer<object?> Comparer = Comparer<object?>.Create((a, b) => a is IComparable c ? c.CompareTo(b) : 0);

    /// <summary>A key that compares byte arrays by content, null included.</summary>
    private readonly record struct GroupKey(object? Value)
    {
        public bool Equals(GroupKey other) => EntityValues.Equal(Value, other.Value);

        public override int GetHashCode() => Value is byte[] bytes ? bytes.Length : Value?.GetHashCode() ?? 0;
    }
}

/// <summary>Starts an <see cref="Aggregation{TEntity}"/>.</summary>
public static class Aggregation
{
    public static Aggregation<TEntity> Of<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>() where TEntity : class => new();
}

/// <summary>
/// One row of an aggregation: the key of its group (null without a group), its number of rows, and the values in the
/// order they were asked for, of the types <see cref="Aggregation{TEntity}.ResultType"/> gives.
/// </summary>
public sealed record AggregateRow(object? Key, long Count, IReadOnlyList<object?> Values);

/// <summary>A group of a typed aggregation: its key, its number of rows and its values.</summary>
public sealed record Group<TKey>(TKey Key, long Count, IReadOnlyList<object?> Values);

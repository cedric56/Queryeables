using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Abstraction;

/// <summary>One sort key: a property of the entity, ascending or descending.</summary>
public sealed record SortKey(string Property, bool Descending = false);

/// <summary>
/// The order of a query, by properties of <typeparamref name="TEntity"/>. Immutable: each method returns a new sort.
/// </summary>
/// <example>
/// <code>
/// var page = await store.SelectPagedAsync&lt;Person&gt;(p => p.Active, 1, 50,
///     Sort&lt;Person&gt;.By(p => p.LastName).ThenBy(p => p.FirstName));
/// </code>
/// </example>
public sealed class Sort<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity> where TEntity : class
{
    /// <summary>Most sort keys in one sort: enough for any screen, and a bound for the requests a server accepts.</summary>
    public const int MaxKeys = 8;

    private Sort(IReadOnlyList<SortKey> keys) => Keys = keys;

    public IReadOnlyList<SortKey> Keys { get; }

    public static Sort<TEntity> By<TValue>(Expression<Func<TEntity, TValue>> property) => new([Key(property, false)]);

    public static Sort<TEntity> ByDescending<TValue>(Expression<Func<TEntity, TValue>> property) => new([Key(property, true)]);

    public Sort<TEntity> ThenBy<TValue>(Expression<Func<TEntity, TValue>> property) => Append(Key(property, false));

    public Sort<TEntity> ThenByDescending<TValue>(Expression<Func<TEntity, TValue>> property) => Append(Key(property, true));

    /// <summary>A sort received by name (from a remote client): every property must be a readable property of the entity.</summary>
    /// <exception cref="ArgumentException">Unknown property, duplicate, or more than <see cref="MaxKeys"/> keys.</exception>
    public static Sort<TEntity> FromKeys(IEnumerable<SortKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var list = keys.ToList();
        if (list.Count == 0 || list.Count > MaxKeys)
            throw new ArgumentException($"A sort has between 1 and {MaxKeys} keys.", nameof(keys));

        foreach (var key in list)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (Property(key.Property) is null)
                throw new ArgumentException($"Unknown property '{key.Property}' on {typeof(TEntity).Name}.", nameof(keys));
        }
        if (list.Select(k => k.Property).Distinct(StringComparer.Ordinal).Count() != list.Count)
            throw new ArgumentException("A property appears twice in the sort.", nameof(keys));

        return new Sort<TEntity>(list);
    }

    /// <summary>Sorts in memory, for stores that cannot sort in the database. Stable: equal rows keep their order.</summary>
    public IEnumerable<TEntity> Apply(IEnumerable<TEntity> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        IOrderedEnumerable<TEntity>? ordered = null;
        foreach (var key in Keys)
        {
            var property = Property(key.Property)!;
            Func<TEntity, object?> value = row => property.GetValue(row);
            ordered = (ordered, key.Descending) switch
            {
                (null, false) => rows.OrderBy(value, ValueComparer.Instance),
                (null, true) => rows.OrderByDescending(value, ValueComparer.Instance),
                (_, false) => ordered.ThenBy(value, ValueComparer.Instance),
                (_, true) => ordered.ThenByDescending(value, ValueComparer.Instance),
            };
        }
        return ordered ?? rows;
    }

    public override string ToString() => string.Join(", ", Keys.Select(k => k.Descending ? $"{k.Property} desc" : k.Property));

    private Sort<TEntity> Append(SortKey key)
    {
        if (Keys.Count >= MaxKeys)
            throw new InvalidOperationException($"A sort is limited to {MaxKeys} keys.");
        if (Keys.Any(k => k.Property == key.Property))
            throw new ArgumentException($"{key.Property} is already in the sort.");
        return new Sort<TEntity>([.. Keys, key]);
    }

    private static SortKey Key<TValue>(Expression<Func<TEntity, TValue>> selector, bool descending)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            body = convert.Operand;

        return body is MemberExpression { Member: PropertyInfo property, Expression: ParameterExpression }
            ? new SortKey(property.Name, descending)
            : throw new ArgumentException($"{selector} must select a property of {typeof(TEntity).Name}, for example x => x.Name.",
                nameof(selector));
    }

    private static PropertyInfo? Property(string name)
        => typeof(TEntity).GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is { CanRead: true } property
           && property.GetIndexParameters().Length == 0
            ? property
            : null;

    /// <summary>Nulls first, like most databases in ascending order; then the natural order of the values.</summary>
    private sealed class ValueComparer : IComparer<object?>
    {
        public static readonly ValueComparer Instance = new();

        public int Compare(object? x, object? y) => Comparer.DefaultInvariant.Compare(x, y);
    }
}

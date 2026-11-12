using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Queryeables.Abstraction;

/// <summary>
/// The values of an entity as its row holds them: what a write changes, compared without a serializer. A graph saves only
/// the children that changed; a patch sends only the properties that changed.
/// </summary>
public static class EntityValues
{
    /// <summary>
    /// The properties stored in the row of <typeparamref name="T"/>: public, readable and writable, of a simple type (numbers,
    /// text, dates, Guid, enums, byte[], and their nullable forms), not [NotMapped]. Navigations and collections are not.
    /// </summary>
    public static IReadOnlyList<PropertyInfo> Columns<[DynamicallyAccessedMembers(EntityMembers.Required)] T>() => Cache<T>.Columns;

    /// <summary>
    /// The columns <typeparamref name="T"/> lets a client change: <see cref="Columns{T}"/> without the key, the version
    /// ([ConcurrencyCheck]) and [LastModified], which the store maintains.
    /// </summary>
    public static IReadOnlyList<PropertyInfo> Writable<[DynamicallyAccessedMembers(EntityMembers.Required)] T>() => Cache<T>.Writable;

    /// <summary>True when every column the client may change (<see cref="Writable{T}"/>) holds the same value in both.</summary>
    public static bool Same<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T a, T b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        foreach (var column in Cache<T>.Writable)
        {
            if (!Equal(column.GetValue(a), column.GetValue(b)))
                return false;
        }
        return true;
    }

    /// <summary>The columns of <paramref name="modified"/> that differ from <paramref name="original"/>.</summary>
    public static IReadOnlyList<PropertyInfo> Changed<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T original, T modified)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        return [.. Cache<T>.Writable.Where(c => !Equal(c.GetValue(original), c.GetValue(modified)))];
    }

    /// <summary>A copy of the columns of <paramref name="entity"/>: a new instance, navigations and collections left out.</summary>
    public static T Copy<[DynamicallyAccessedMembers(EntityMembers.Required | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(T entity)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(entity);
        var copy = new T();
        foreach (var column in Cache<T>.Columns)
            column.SetValue(copy, column.GetValue(entity));
        return copy;
    }

    /// <summary>
    /// The columns of <typeparamref name="T"/> that <paramref name="selector"/> reads; null when it reads the entity otherwise
    /// (a navigation, the whole entity): a store then reads whole rows.
    /// </summary>
    public static IReadOnlyList<PropertyInfo>? ColumnsReadBy<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(System.Linq.Expressions.LambdaExpression selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var finder = new ColumnFinder(selector.Parameters[0], Cache<T>.Columns);
        finder.Visit(selector.Body);
        return finder.Other || finder.Columns.Count == 0 ? null : [.. finder.Columns];
    }

    private sealed class ColumnFinder(System.Linq.Expressions.ParameterExpression parameter, PropertyInfo[] columns) : System.Linq.Expressions.ExpressionVisitor
    {
        public HashSet<PropertyInfo> Columns { get; } = [];
        public bool Other { get; private set; }

        protected override System.Linq.Expressions.Expression VisitMember(System.Linq.Expressions.MemberExpression node)
        {
            if (node.Expression != parameter)
                return base.VisitMember(node);
            if (node.Member is PropertyInfo property && columns.FirstOrDefault(c => c.Name == property.Name) is { } column)
                Columns.Add(column);
            else
                Other = true;
            return node;
        }

        protected override System.Linq.Expressions.Expression VisitParameter(System.Linq.Expressions.ParameterExpression node)
        {
            if (node == parameter)
                Other = true;
            return node;
        }
    }

    /// <summary>Values compared as the database does: byte arrays by content.</summary>
    public static bool Equal(object? a, object? b)
        => a is byte[] x && b is byte[] y ? x.AsSpan().SequenceEqual(y) : Equals(a, b);

    /// <summary>A type a column holds.</summary>
    public static bool IsSimple(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var core = Nullable.GetUnderlyingType(type) ?? type;
        return core.IsPrimitive || core.IsEnum || core == typeof(string) || core == typeof(decimal) || core == typeof(DateTime)
               || core == typeof(DateTimeOffset) || core == typeof(TimeSpan) || core == typeof(Guid) || core == typeof(DateOnly)
               || core == typeof(TimeOnly) || core == typeof(byte[]);
    }

    private static class Cache<[DynamicallyAccessedMembers(EntityMembers.Required)] T>
    {
        public static readonly PropertyInfo[] Columns = [.. typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && IsSimple(p.PropertyType)
                        && p.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute>() is null)];

        public static readonly PropertyInfo[] Writable = [.. Columns.Where(p =>
            !SyncMetadata.Keys(typeof(T)).Contains(p)
            && p != ConcurrencyToken.Find(typeof(T))
            && p != SyncMetadata.LastModified(typeof(T)))];
    }
}

/// <summary>
/// The values entities had when they were read: what a later save compares with to know what this user changed. A graph
/// remembers the children it loads; a child that still has those values is left alone when the graph is saved, even if
/// someone else changed its row meanwhile (two users editing different lines of one order do not conflict).
/// </summary>
/// <remarks>Held weakly: forgotten with the entity. Copies of the columns only.</remarks>
public static class EntityOriginals
{
    private static readonly ConditionalWeakTable<object, object?[]> Values = new();

    /// <summary>Remembers the current values of <paramref name="entity"/> as what was read.</summary>
    public static void Remember<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var columns = EntityValues.Writable<T>();
        var values = new object?[columns.Count];
        for (var i = 0; i < values.Length; i++)
            values[i] = Snapshot(columns[i].GetValue(entity));
        Values.AddOrUpdate(entity, values);
    }

    /// <summary>Forgets what was read: the next save compares <paramref name="entity"/> with the stored row.</summary>
    public static void Forget(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        Values.Remove(entity);
    }

    /// <summary>True when <paramref name="entity"/> was read and still has the values it was read with.</summary>
    public static bool IsUnchanged<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!Values.TryGetValue(entity, out var values))
            return false;
        var columns = EntityValues.Writable<T>();
        for (var i = 0; i < values.Length; i++)
        {
            if (!EntityValues.Equal(values[i], columns[i].GetValue(entity)))
                return false;
        }
        return true;
    }

    private static object? Snapshot(object? value) => value is byte[] bytes ? bytes.Clone() : value;
}

/// <summary>Values assigned to every row an <c>UpdateWhereAsync</c> changes: constants, the same for each row.</summary>
public sealed class EntityUpdate<[DynamicallyAccessedMembers(EntityMembers.Required)] T> where T : class
{
    private readonly List<(PropertyInfo Property, object? Value)> _values = [];

    /// <summary>What is assigned, in order.</summary>
    public IReadOnlyList<(PropertyInfo Property, object? Value)> Values => _values;

    /// <summary>Sets <paramref name="property"/> to <paramref name="value"/> on each row.</summary>
    public EntityUpdate<T> Set<TValue>(System.Linq.Expressions.Expression<Func<T, TValue>> property, TValue value)
    {
        ArgumentNullException.ThrowIfNull(property);
        var body = property.Body is System.Linq.Expressions.UnaryExpression { NodeType: System.Linq.Expressions.ExpressionType.Convert } convert
            ? convert.Operand : property.Body;
        if (body is not System.Linq.Expressions.MemberExpression { Member: PropertyInfo selected } member
            || member.Expression != property.Parameters[0]
            || !EntityValues.Columns<T>().Contains(selected))
            throw new ArgumentException($"{property} must select a column of {typeof(T).Name}, for example e => e.Archived.", nameof(property));
        _values.Add((selected, value));
        return this;
    }

    /// <summary>Assigns the values to <paramref name="entity"/>.</summary>
    public void Apply(T entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        foreach (var (property, value) in _values)
            property.SetValue(entity, value);
    }
}

/// <summary>A patch: the columns one user changed, written over the stored row.</summary>
public static class EntityPatch
{
    /// <summary>
    /// Writes into <paramref name="stored"/> the values of <paramref name="changed"/> taken from <paramref name="modified"/>.
    /// A column whose stored value is neither the original nor the new one was changed by someone else: a conflict. False
    /// when nothing is left to write (every change is already stored).
    /// </summary>
    public static bool Merge<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T stored, T original, T modified, IEnumerable<PropertyInfo> changed)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        ArgumentNullException.ThrowIfNull(changed);
        var writes = false;
        foreach (var column in changed)
        {
            var now = column.GetValue(stored);
            var wanted = column.GetValue(modified);
            if (EntityValues.Equal(now, wanted))
                continue;
            if (!EntityValues.Equal(now, column.GetValue(original)))
                throw new EntityStoreException(EntityErrorCode.Conflict,
                    $"{typeof(T).Name}.{column.Name} was changed by someone else meanwhile.");
            column.SetValue(stored, wanted);
            writes = true;
        }
        return writes;
    }

    /// <summary>Copies every column (key, version and [LastModified] included) of <paramref name="from"/> into <paramref name="to"/>.</summary>
    public static void CopyColumns<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T from, T to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        foreach (var column in EntityValues.Columns<T>())
            column.SetValue(to, column.GetValue(from));
    }
}

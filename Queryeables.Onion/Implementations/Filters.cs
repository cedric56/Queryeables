using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Onion;

/// <summary>
/// Conversion entre prédicats LINQ et filtres transportables par MagicOnion.
/// Client : prédicat → filtres. Serveur : filtres → prédicat.
/// </summary>
internal static class Filters
{
    // ---------- Client : prédicat → filtres ----------

    /// <summary>
    /// Traduit un prédicat de la forme <c>e => e.A == x &amp;&amp; e.B == y</c> (ou avec ||) en filtres.
    /// Formes acceptées : égalités entre une propriété et une valeur, propriétés booléennes (<c>e.Actif</c>,
    /// <c>!e.Actif</c>), et <c>e => true</c>. Tout le reste lève <see cref="NotSupportedException"/>.
    /// </summary>
    public static (Dictionary<string, object?> Filters, bool Any) FromPredicate<T>(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var parameter = predicate.Parameters[0];
        var filters = new Dictionary<string, object?>();

        if (predicate.Body is ConstantExpression { Value: true })
            return (filters, false);

        bool? any = null;
        var terms = new List<Expression>();
        Flatten(predicate.Body, terms, ref any);

        foreach (var term in terms)
        {
            var (name, value) = ParseTerm(term, parameter);
            value = Normalize(value);

            if (filters.TryAdd(name, value))
                continue;

            // En OU, plusieurs valeurs pour une même propriété deviennent une liste (équivalent d'un IN).
            if (any != true)
                throw Unsupported($"la propriété {name} est comparée plusieurs fois dans un ET");

            if (filters[name] is List<object?> values)
                values.Add(value);
            else
                filters[name] = new List<object?> { filters[name], value };
        }

        return (filters, any ?? false);
    }

    private static void Flatten(Expression expression, List<Expression> terms, ref bool? any)
    {
        if (expression.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
        {
            var isOr = expression.NodeType == ExpressionType.OrElse;
            if (any is not null && any != isOr)
                throw Unsupported("ET et OU ne peuvent pas être mélangés");
            any = isOr;

            var binary = (BinaryExpression)expression;
            Flatten(binary.Left, terms, ref any);
            Flatten(binary.Right, terms, ref any);
        }
        else
        {
            terms.Add(expression);
        }
    }

    private static (string Name, object? Value) ParseTerm(Expression term, ParameterExpression parameter)
    {
        switch (term)
        {
            case BinaryExpression { NodeType: ExpressionType.Equal } equal:
                if (AsProperty(equal.Left, parameter) is { } left && !UsesParameter(equal.Right, parameter))
                    return (left.Name, Evaluate(equal.Right));
                if (AsProperty(equal.Right, parameter) is { } right && !UsesParameter(equal.Left, parameter))
                    return (right.Name, Evaluate(equal.Left));
                break;

            case UnaryExpression { NodeType: ExpressionType.Not } negation
                when AsProperty(negation.Operand, parameter) is { } negated && IsBoolean(negated):
                return (negated.Name, false);

            default:
                if (AsProperty(term, parameter) is { } flag && IsBoolean(flag))
                    return (flag.Name, true);
                break;
        }

        throw Unsupported($"« {term} » n'est pas une égalité entre une propriété et une valeur");
    }

    /// <summary>Renvoie la propriété si l'expression est <c>e.Propriete</c> (éventuellement convertie).</summary>
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

    private static object? Evaluate(Expression expression)
        => expression is ConstantExpression constant
            ? constant.Value
            : Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object))).Compile()();

    /// <summary>Ramène la valeur à un type primitif que MessagePack transporte sans résolveur particulier.</summary>
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
        => new($"Prédicat non transmissible au serveur : {reason}. " +
               "Seules les égalités propriété = valeur, combinées toutes en ET ou toutes en OU, sont prises en charge.");

    private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter) Found = true;
            return node;
        }
    }

    // ---------- Serveur : filtres → prédicat ----------

    /// <summary>Construit <c>e => e.A == a &amp;&amp; e.B == b</c> (ou avec || si <paramref name="any"/>).</summary>
    public static Expression<Func<T, bool>> ToPredicate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        IReadOnlyDictionary<string, object?> filters, bool any)
    {
        var parameter = Expression.Parameter(typeof(T), "e");
        Expression? body = null;

        foreach (var (name, value) in filters)
        {
            var property = typeof(T).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Propriété inconnue « {name} » sur {typeof(T).Name}.");

            var member = Expression.Property(parameter, property);
            var equal = AsValueList(value) is { } values
                ? AnyOf(member, values, property.PropertyType, name)
                : Equal(member, Expression.Constant(ConvertTo(value, property.PropertyType, name), property.PropertyType));

            body = body is null ? equal
                : any ? Expression.OrElse(body, equal)
                : Expression.AndAlso(body, equal);
        }

        body ??= Expression.Constant(true);
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    /// <summary>
    /// Convertit une valeur reçue vers le type de la propriété. MessagePack ne conserve pas le type exact
    /// d'un nombre rangé dans un object (un 5 revient en byte) : sans conversion, Expression.Constant échoue.
    /// </summary>
    public static object? ConvertTo(object? value, Type type, string name)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (value is null)
        {
            if (type.IsValueType && target == type)
                throw new ArgumentException($"La propriété « {name} » ({type.Name}) n'accepte pas null.");
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
                $"Valeur « {value} » ({value.GetType().Name}) invalide pour la propriété « {name} » ({type.Name}).", ex);
        }
    }

    /// <summary>
    /// Une liste de valeurs (reçue en object[] après MessagePack) signifie « l'une de ces valeurs ».
    /// Un byte[] reste une valeur unique (Guid binaire, blob).
    /// </summary>
    private static System.Collections.IList? AsValueList(object? value)
        => value is System.Collections.IList list and not byte[] ? list : null;

    /// <summary>Construit <c>e.P == v1 || e.P == v2 || ...</c>.</summary>
    private static Expression AnyOf(MemberExpression member, System.Collections.IList values, Type type, string name)
    {
        if (values.Count == 0)
            throw new ArgumentException($"Liste de valeurs vide pour la propriété « {name} ».");

        Expression? body = null;
        foreach (var value in values)
        {
            var equal = Equal(member, Expression.Constant(ConvertTo(value, type, name), type));
            body = body is null ? equal : Expression.OrElse(body, equal);
        }
        return body!;
    }

    /// <summary>Égalité, y compris pour les enums, que les arbres d'expressions ne comparent pas directement.</summary>
    private static Expression Equal(Expression left, Expression right)
    {
        var underlying = Nullable.GetUnderlyingType(left.Type);
        var core = underlying ?? left.Type;
        if (!core.IsEnum)
            return Expression.Equal(left, right);

        var numeric = Enum.GetUnderlyingType(core);
        var target = underlying is null ? numeric : typeof(Nullable<>).MakeGenericType(numeric);
        return Expression.Equal(Expression.Convert(left, target), Expression.Convert(right, target));
    }
}

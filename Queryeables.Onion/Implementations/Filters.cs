using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Onion;

/// <summary>
/// Conversion entre prédicats LINQ et conditions transportables par MagicOnion.
/// Client : prédicat → conditions. Serveur : conditions → prédicat, sur une liste blanche d'opérations.
/// </summary>
internal static class Filters
{
    private static readonly MethodInfo StringStartsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo StringEndsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;
    private static readonly MethodInfo StringContains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;

    // =====================================================================
    // Client : prédicat → conditions
    // =====================================================================

    /// <summary>
    /// Traduit un prédicat en conditions. Formes acceptées, combinées toutes en ET ou toutes en OU :
    /// <list type="bullet">
    /// <item>comparaisons entre une propriété et une valeur : ==, !=, &gt;, &gt;=, &lt;, &lt;= ;</item>
    /// <item>propriété booléenne : <c>e.Actif</c>, <c>!e.Actif</c> ;</item>
    /// <item>appartenance à une liste : <c>ids.Contains(e.Id)</c> ;</item>
    /// <item>texte : <c>e.Nom.StartsWith(x)</c>, <c>EndsWith</c>, <c>Contains</c> ;</item>
    /// <item><c>e => true</c>.</item>
    /// </list>
    /// Tout le reste lève <see cref="NotSupportedException"/>, avant tout appel au serveur.
    /// </summary>
    public static (FilterCondition[] Conditions, bool Any) FromPredicate<T>(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var parameter = predicate.Parameters[0];

        if (predicate.Body is ConstantExpression { Value: true })
            return ([], false);

        bool? any = null;
        var terms = new List<Expression>();
        Flatten(predicate.Body, terms, ref any);

        var conditions = terms.Select(term => ParseTerm(term, parameter)).ToArray();
        return (conditions, any ?? false);
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

        throw Unsupported($"« {term} » n'est pas une condition prise en charge");
    }

    private static FilterCondition? ParseMethodCall(MethodCallExpression call, ParameterExpression parameter)
    {
        // e.Nom.StartsWith(x), EndsWith(x), Contains(x)
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

        // liste.Contains(e.Id) : Enumerable.Contains(liste, e.Id), MemoryExtensions.Contains(span, e.Id)
        // (forme produite par C# 14 pour un tableau) ou méthode d'instance d'une collection.
        var (source, item) = call.Object is null && call.Arguments.Count == 2
            ? (call.Arguments[0], call.Arguments[1])
            : (call.Object, call.Arguments.Count == 1 ? call.Arguments[0] : null);

        // "abc".Contains(e.Nom) n'est pas une liste de valeurs : un texte n'est pas une collection ici.
        if (source is null || item is null || source.Type == typeof(string)
            || AsProperty(item, parameter) is not { } member || UsesParameter(source, parameter))
            return null;

        var values = Evaluate(UnwrapSpan(source)) as IEnumerable
            ?? throw Unsupported($"« {source} » n'est pas une collection");
        return new FilterCondition(member.Name, FilterOperator.In, values.Cast<object?>().Select(Normalize).ToArray());
    }

    /// <summary>Un tableau converti en ReadOnlySpan ne peut pas être évalué : on évalue le tableau d'origine.</summary>
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

    /// <summary><c>5 &lt; e.Id</c> devient <c>e.Id &gt; 5</c>.</summary>
    private static FilterOperator Mirror(FilterOperator op) => op switch
    {
        FilterOperator.GreaterThan => FilterOperator.LessThan,
        FilterOperator.GreaterThanOrEqual => FilterOperator.LessThanOrEqual,
        FilterOperator.LessThan => FilterOperator.GreaterThan,
        FilterOperator.LessThanOrEqual => FilterOperator.GreaterThanOrEqual,
        _ => op
    };

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
        => new($"Prédicat non transmissible au serveur : {reason}. Sont pris en charge : comparaisons entre une " +
               "propriété et une valeur, propriétés booléennes, liste.Contains(e.Prop), StartsWith/EndsWith/Contains " +
               "sur du texte, combinés tous en ET ou tous en OU.");

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
    // Serveur : conditions → prédicat
    // =====================================================================

    /// <summary>Construit le prédicat, en refusant toute condition hors de la liste blanche.</summary>
    public static Expression<Func<T, bool>> ToPredicate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        IReadOnlyList<FilterCondition> conditions, bool any)
    {
        var parameter = Expression.Parameter(typeof(T), "e");
        Expression? body = null;

        foreach (var condition in conditions)
        {
            ArgumentNullException.ThrowIfNull(condition);
            var property = typeof(T).GetProperty(condition.Property, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Propriété inconnue « {condition.Property} » sur {typeof(T).Name}.");

            var term = Build(Expression.Property(parameter, property), condition);
            body = body is null ? term
                : any ? Expression.OrElse(body, term)
                : Expression.AndAlso(body, term);
        }

        body ??= Expression.Constant(true);
        return Expression.Lambda<Func<T, bool>>(body, parameter);
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
                    throw new ArgumentException($"« {name} » : une liste n'est acceptée qu'avec l'opérateur In.");
                return Compare(member, Constant(condition.Value, type, name), condition.Operator, name);

            case FilterOperator.In:
                if (condition.Value is not IList values || condition.Value is byte[])
                    throw new ArgumentException($"« {name} » : l'opérateur In attend une liste de valeurs.");
                if (values.Count == 0)
                    return Expression.Constant(false); // aucune valeur : aucune ligne
                Expression? anyOf = null;
                foreach (var value in values)
                {
                    var equal = Compare(member, Constant(value, type, name), FilterOperator.Equal, name);
                    anyOf = anyOf is null ? equal : Expression.OrElse(anyOf, equal);
                }
                return anyOf!;

            case FilterOperator.StartsWith or FilterOperator.EndsWith or FilterOperator.Contains:
                if (type != typeof(string))
                    throw new ArgumentException($"« {name} » n'est pas du texte : {condition.Operator} impossible.");
                if (condition.Value is not string text)
                    throw new ArgumentException($"« {name} » : {condition.Operator} attend un texte.");
                var method = condition.Operator switch
                {
                    FilterOperator.StartsWith => StringStartsWith,
                    FilterOperator.EndsWith => StringEndsWith,
                    _ => StringContains
                };
                return Expression.Call(member, method, Expression.Constant(text));

            default:
                throw new ArgumentException($"Opérateur inconnu : {condition.Operator}.");
        }
    }

    private static ConstantExpression Constant(object? value, Type type, string name)
        => Expression.Constant(ConvertTo(value, type, name), type);

    /// <summary>Comparaison, y compris pour les enums, que les arbres d'expressions ne comparent pas directement.</summary>
    private static Expression Compare(Expression left, Expression right, FilterOperator op, string name)
    {
        var underlying = Nullable.GetUnderlyingType(left.Type);
        var core = underlying ?? left.Type;
        if (core.IsEnum)
        {
            var numeric = Enum.GetUnderlyingType(core);
            var target = underlying is null ? numeric : typeof(Nullable<>).MakeGenericType(numeric);
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
                _ => throw new ArgumentException($"Opérateur inattendu : {op}.")
            };
        }
        catch (InvalidOperationException ex)
        {
            // Par exemple « Nom > 'A' » : pas d'opérateur de comparaison sur le texte.
            throw new ArgumentException($"« {name} » ({left.Type.Name}) ne peut pas être comparé avec {op}.", ex);
        }
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
}

using Queryeables.Abstraction;
using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Remote;

/// <summary>
/// The filters a client sends beyond "a property compared with a value": comparisons between properties, computations
/// (arithmetic, text and date functions), conditions through relations (<c>Any</c>, <c>All</c>, <c>Count</c> on a
/// collection, a property of a navigation). Postfix, in the same <see cref="FilterCondition"/> array as the simple form:
/// values (<see cref="FilterOperator.Member"/>, <see cref="FilterOperator.Value"/>) and operations on them. The server binds
/// each name to a property the client may filter on, of a type it exposes: nothing else reaches the store.
/// </summary>
internal static class FilterLanguage
{
    /// <summary>The functions a filter may call, with their number of arguments.</summary>
    private static readonly Dictionary<string, int> Functions = new(StringComparer.Ordinal)
    {
        ["lower"] = 1, ["upper"] = 1, ["trim"] = 1, ["trimstart"] = 1, ["trimend"] = 1, ["length"] = 1,
        ["year"] = 1, ["month"] = 1, ["day"] = 1, ["hour"] = 1, ["minute"] = 1, ["second"] = 1, ["dayofweek"] = 1, ["dayofyear"] = 1,
        ["date"] = 1, ["abs"] = 1, ["floor"] = 1, ["ceiling"] = 1, ["compare"] = 2, ["concat"] = 2, ["substring"] = 3, ["substringfrom"] = 2,
    };

    // =====================================================================
    // Client: expression → postfix
    // =====================================================================

    /// <summary>The predicate, in the extended form.</summary>
    public static FilterCondition[] Encode(LambdaExpression predicate)
    {
        var output = new List<FilterCondition>();
        new Encoder(output, predicate.Parameters[0]).Condition(predicate.Body, predicate.Parameters[0]);
        if (output.Count > Filters.MaxValues)
            throw Unsupported($"more than {Filters.MaxValues} conditions");
        return [.. output];
    }

    /// <summary>A value computed from the entity (the key of a group, the value of an aggregate), in the extended form.</summary>
    public static void EncodeValue(LambdaExpression value, List<FilterCondition> output)
        => new Encoder(output, value.Parameters[0]).Value(value.Body, value.Parameters[0]);

    private sealed class Encoder(List<FilterCondition> output, ParameterExpression root)
    {
        private readonly HashSet<ParameterExpression> _scopes = [root];

        private void Add(FilterOperator op, string property = "", object? value = null) => output.Add(new FilterCondition(property, op, value));

        public void Condition(Expression expression, ParameterExpression scope)
        {
            expression = StripConvert(expression, keepBool: true);
            switch (expression)
            {
                case BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } logical:
                {
                    var terms = new List<Expression>();
                    Flatten(logical, logical.NodeType, terms);
                    foreach (var term in terms)
                        Condition(term, scope);
                    Add(logical.NodeType == ExpressionType.AndAlso ? FilterOperator.And : FilterOperator.Or, value: terms.Count);
                    return;
                }
                case UnaryExpression { NodeType: ExpressionType.Not } not:
                    Condition(not.Operand, scope);
                    Add(FilterOperator.Not);
                    return;
                case BinaryExpression binary when Comparison(binary.NodeType) is { } op:
                    Value(binary.Left, scope);
                    Value(binary.Right, scope);
                    Add(op);
                    return;
                case MethodCallExpression call when Call(call, scope):
                    return;
            }

            if (!UsesScopes(expression))
            {
                Add(FilterOperator.Value, value: Evaluate(expression) is true);
                Add(FilterOperator.Value, value: true);
                Add(FilterOperator.Equal);
                return;
            }
            if ((Nullable.GetUnderlyingType(expression.Type) ?? expression.Type) == typeof(bool))
            {
                Value(expression, scope);
                Add(FilterOperator.Value, value: true);
                Add(FilterOperator.Equal);                 // a flag: e.Active, e.Customer.Active
                return;
            }
            throw Unsupported($"'{expression}' is not a condition");
        }

        private bool Call(MethodCallExpression call, ParameterExpression scope)
        {
            var method = call.Method;
            if (method.DeclaringType == typeof(string) && !method.IsStatic && call.Arguments.Count == 1 && call.Arguments[0].Type == typeof(string)
                && method.Name is nameof(string.StartsWith) or nameof(string.EndsWith) or nameof(string.Contains))
            {
                Value(call.Object!, scope);
                Value(call.Arguments[0], scope);
                Add(method.Name switch
                {
                    nameof(string.StartsWith) => FilterOperator.StartsWith,
                    nameof(string.EndsWith) => FilterOperator.EndsWith,
                    _ => FilterOperator.Contains,
                });
                return true;
            }
            if (method.DeclaringType == typeof(string) && method.Name == nameof(string.IsNullOrEmpty) && call.Arguments.Count == 1)
            {
                Value(call.Arguments[0], scope);
                Add(FilterOperator.Value);
                Add(FilterOperator.Equal);
                Value(call.Arguments[0], scope);
                Add(FilterOperator.Value, value: string.Empty);
                Add(FilterOperator.Equal);
                Add(FilterOperator.Or, value: 2);
                return true;
            }
            if (method.DeclaringType == typeof(Enumerable) && method.Name is "Any" or "All" && call.Arguments.Count is 1 or 2
                && Path(call.Arguments[0], scope) is { } relation)
            {
                var inner = call.Arguments.Count == 2 ? StripQuote(call.Arguments[1]) as LambdaExpression : null;
                if (call.Arguments.Count == 2 && inner is null)
                    throw Unsupported($"'{call}': the condition of a relation is a lambda written in place");
                if (inner is not null)
                {
                    _scopes.Add(inner.Parameters[0]);
                    Condition(inner.Body, inner.Parameters[0]);
                }
                if (method.Name == "All" && inner is null)
                    throw Unsupported($"'{call}' needs a condition");
                Add(method.Name == "Any" ? FilterOperator.Any : FilterOperator.All, relation, inner is null ? 0 : 1);
                return true;
            }
            if (method.Name == "Contains" && List(call) is { } list)
            {
                Value(list.Item, scope);
                Add(FilterOperator.In, value: list.Values);
                return true;
            }
            if (method.Name == nameof(Equals) && call.Object is not null && call.Arguments.Count == 1)
            {
                Value(call.Object, scope);
                Value(call.Arguments[0], scope);
                Add(FilterOperator.Equal);
                return true;
            }
            return false;
        }

        public void Value(Expression expression, ParameterExpression scope)
        {
            expression = StripConvert(expression, keepBool: false);
            if (!UsesScopes(expression))
            {
                Add(FilterOperator.Value, value: Normalize(Evaluate(expression)));
                return;
            }

            if (Path(expression, scope) is { } path)
            {
                Add(FilterOperator.Member, path);
                return;
            }

            switch (expression)
            {
                case MemberExpression { Member.Name: "Value", Expression: { } nullable } when Nullable.GetUnderlyingType(nullable.Type) is not null:
                    Value(nullable, scope);
                    return;
                case MemberExpression { Member.Name: "Length", Expression: { } text } when text.Type == typeof(string):
                    Function("length", scope, text);
                    return;
                case MemberExpression { Expression: { } date } member when IsDate(date.Type)
                    && member.Member.Name is "Year" or "Month" or "Day" or "Hour" or "Minute" or "Second" or "DayOfWeek" or "DayOfYear" or "Date":
                    Function(member.Member.Name.ToLowerInvariant(), scope, date);
                    return;
                case BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce:
                    Value(coalesce.Left, scope);
                    Value(coalesce.Right, scope);
                    Add(FilterOperator.Coalesce);
                    return;
                case BinaryExpression { NodeType: ExpressionType.Add } concat when concat.Type == typeof(string):
                    Function("concat", scope, concat.Left, concat.Right);
                    return;
                case BinaryExpression arithmetic when Arithmetic(arithmetic.NodeType) is { } op:
                    Value(arithmetic.Left, scope);
                    Value(arithmetic.Right, scope);
                    Add(op);
                    return;
                case MethodCallExpression call:
                    ValueCall(call, scope);
                    return;
            }
            throw Unsupported($"'{expression}' cannot be computed by the server");
        }

        private void ValueCall(MethodCallExpression call, ParameterExpression scope)
        {
            var method = call.Method;
            var name = method.Name;
            if (method.DeclaringType == typeof(string) && call.Object is { } text)
            {
                switch (name)
                {
                    case nameof(string.ToLower) or nameof(string.ToLowerInvariant) when call.Arguments.Count == 0:
                        Function("lower", scope, text);
                        return;
                    case nameof(string.ToUpper) or nameof(string.ToUpperInvariant) when call.Arguments.Count == 0:
                        Function("upper", scope, text);
                        return;
                    case nameof(string.Trim) or nameof(string.TrimStart) or nameof(string.TrimEnd) when call.Arguments.Count == 0:
                        Function(name.ToLowerInvariant(), scope, text);
                        return;
                    case nameof(string.Substring) when call.Arguments.Count == 2:
                        Function("substring", scope, text, call.Arguments[0], call.Arguments[1]);
                        return;
                    case nameof(string.Substring) when call.Arguments.Count == 1:
                        Function("substringfrom", scope, text, call.Arguments[0]);
                        return;
                    case nameof(string.CompareTo) when call.Arguments.Count == 1:
                        Function("compare", scope, text, call.Arguments[0]);
                        return;
                }
            }
            if (method.DeclaringType == typeof(string) && method.IsStatic && call.Arguments.Count == 2)
            {
                switch (name)
                {
                    case nameof(string.Compare) when call.Arguments.All(a => a.Type == typeof(string)):
                        Function("compare", scope, call.Arguments[0], call.Arguments[1]);
                        return;
                    case nameof(string.Concat) when call.Arguments.All(a => a.Type == typeof(string)):
                        Function("concat", scope, call.Arguments[0], call.Arguments[1]);
                        return;
                }
            }
            if (method.DeclaringType == typeof(Math) && call.Arguments.Count == 1 && name is nameof(Math.Abs) or nameof(Math.Floor) or nameof(Math.Ceiling))
            {
                Function(name.ToLowerInvariant(), scope, call.Arguments[0]);
                return;
            }
            if (method.DeclaringType == typeof(Enumerable) && name is "Count" or "LongCount" && call.Arguments.Count is 1 or 2
                && Path(call.Arguments[0], scope) is { } relation)
            {
                var inner = call.Arguments.Count == 2 ? StripQuote(call.Arguments[1]) as LambdaExpression : null;
                if (inner is not null)
                {
                    _scopes.Add(inner.Parameters[0]);
                    Condition(inner.Body, inner.Parameters[0]);
                }
                Add(FilterOperator.Count, relation, inner is null ? 0 : 1);
                return;
            }
            throw Unsupported($"'{call}' cannot be computed by the server");
        }

        private void Function(string name, ParameterExpression scope, params Expression[] arguments)
        {
            foreach (var argument in arguments)
                Value(argument, scope);
            Add(FilterOperator.Function, name, arguments.Length);
        }

        /// <summary><c>e.Customer.City</c> from the parameter of the scope: "Customer.City". Null when the expression is not a path.</summary>
        private static string? Path(Expression expression, ParameterExpression scope)
        {
            var names = new List<string>();
            expression = StripConvert(expression, keepBool: false);
            while (expression is MemberExpression { Member: PropertyInfo property } member)
            {
                if (property.Name == "Value" && Nullable.GetUnderlyingType(member.Expression!.Type) is not null)
                    return null;
                names.Insert(0, property.Name);
                expression = StripConvert(member.Expression!, keepBool: false);
                if (expression == scope)
                    return string.Join(".", names);
                if (IsScalarFunctionOwner(member.Expression!.Type))
                    return null;          // a property of a value (Length of a text, Year of a date): a function
            }
            return null;
        }

        private static bool IsScalarFunctionOwner(Type type) => type == typeof(string) || IsDate(type);

        private bool UsesScopes(Expression expression)
        {
            var finder = new ParameterFinder(_scopes);
            finder.Visit(expression);
            return finder.Found;
        }

        private (Expression Item, object?[] Values)? List(MethodCallExpression call)
        {
            var (source, item) = call.Object is null && (call.Arguments.Count == 2 || call.Arguments.Count == 3 && call.Arguments[2] is ConstantExpression { Value: null })
                ? (call.Arguments[0], call.Arguments[1])
                : (call.Object, call.Arguments.Count == 1 ? call.Arguments[0] : null);
            if (source is null || item is null || source.Type == typeof(string) || UsesScopes(source) || !UsesScopes(item))
                return null;
            var values = Evaluate(UnwrapSpan(source)) as IEnumerable ?? throw Unsupported($"'{source}' is not a collection");
            var list = values.Cast<object?>().Select(Normalize).ToArray();
            if (list.Length > Filters.MaxValues)
                throw Unsupported($"more than {Filters.MaxValues} values in a list");
            return (item, list);
        }
    }

    // =====================================================================
    // Server: postfix → expression
    // =====================================================================

    /// <summary>True when the conditions use the extended form (values and operations on them).</summary>
    public static bool IsExtended(IReadOnlyList<FilterCondition> conditions)
        => conditions.Any(c => c is not null && (c.Operator >= FilterOperator.Member
            || c.Operator is not (FilterOperator.And or FilterOperator.Or) && string.IsNullOrEmpty(c.Property)));

    /// <summary>The predicate on the root type of <paramref name="scope"/>; every name is bound through the scopes.</summary>
    public static Expression Bind(IReadOnlyList<FilterCondition> conditions, IFilterScope scope, ParameterExpression parameter)
    {
        var node = Parse(conditions);
        return AsCondition(new Binder().Bind(node, scope, parameter, 0), "the filter");
    }

    /// <summary>The values of an aggregation: each ends with a marker (<see cref="FilterOperator.GroupKey"/> or <see cref="FilterOperator.Aggregate"/>).</summary>
    public static (Expression? Key, List<(AggregateFunction Function, Expression? Value)> Values) BindAggregation(
        IReadOnlyList<FilterCondition> conditions, IFilterScope scope, ParameterExpression parameter)
    {
        if (conditions.Count > Filters.MaxValues)
            throw new ArgumentException($"An aggregation is limited to {Filters.MaxValues} nodes.");
        var stack = new Stack<Node>();
        Expression? key = null;
        var values = new List<(AggregateFunction, Expression?)>();
        var binder = new Binder();
        foreach (var condition in conditions)
        {
            ArgumentNullException.ThrowIfNull(condition);
            switch (condition.Operator)
            {
                case FilterOperator.GroupKey:
                    if (key is not null || stack.Count != 1)
                        throw new ArgumentException("One key per aggregation, from one value.");
                    key = binder.Typed(binder.Bind(stack.Pop(), scope, parameter, 0));
                    break;
                case FilterOperator.Aggregate:
                    if (!Enum.TryParse<AggregateFunction>(condition.Property, out var function) || !Enum.IsDefined(function))
                        throw new ArgumentException($"Unknown aggregate '{condition.Property}'.");
                    if (function == AggregateFunction.Count)
                    {
                        if (stack.Count != 0)
                            throw new ArgumentException("Count takes no value.");
                        values.Add((function, null));
                    }
                    else
                    {
                        if (stack.Count != 1)
                            throw new ArgumentException($"{function} takes one value.");
                        values.Add((function, binder.Typed(binder.Bind(stack.Pop(), scope, parameter, 0))));
                    }
                    break;
                default:
                    Push(stack, condition);
                    break;
            }
        }
        if (stack.Count != 0 || values.Count == 0 && key is null)
            throw new ArgumentException("The aggregation does not end with its values.");
        return (key, values);
    }

    // ---------- Parsing ----------

    private abstract record Node;

    private sealed record Leaf(FilterCondition Condition) : Node;

    private sealed record ValueNode(object? Value) : Node;

    private sealed record MemberNode(string Path) : Node;

    private sealed record Operation(FilterOperator Operator, Node[] Arguments, string Name = "", object? Extra = null) : Node;

    private sealed record RelationNode(FilterOperator Operator, string Path, Node? Inner) : Node;

    private static Node Parse(IReadOnlyList<FilterCondition> conditions)
    {
        if (conditions.Count > Filters.MaxValues)
            throw new ArgumentException($"A filter is limited to {Filters.MaxValues} conditions.");
        var stack = new Stack<Node>();
        foreach (var condition in conditions)
        {
            ArgumentNullException.ThrowIfNull(condition);
            Push(stack, condition);
        }
        if (stack.Count != 1)
            throw new ArgumentException("The filter does not combine into a single condition.");
        return stack.Pop();
    }

    private static void Push(Stack<Node> stack, FilterCondition condition)
    {
        Node[] Pop(int count)
        {
            if (count < 0 || count > stack.Count)
                throw new ArgumentException($"{condition.Operator} expects {count} operands, the filter has {stack.Count}.");
            var operands = new Node[count];
            for (var i = count - 1; i >= 0; i--)
                operands[i] = stack.Pop();
            return operands;
        }

        int Count()
        {
            try
            {
                return Convert.ToInt32(Filters.Plain(condition.Value), CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                throw new ArgumentException($"{condition.Operator} expects a number of operands.", ex);
            }
        }

        var simple = !string.IsNullOrEmpty(condition.Property);
        switch (condition.Operator)
        {
            case FilterOperator.Member:
                if (!simple)
                    throw new ArgumentException("A member names its property.");
                stack.Push(new MemberNode(condition.Property));
                return;
            case FilterOperator.Value:
                stack.Push(new ValueNode(Filters.Plain(condition.Value)));
                return;
            case FilterOperator.And or FilterOperator.Or:
                var count = Count();
                if (count < 2)
                    throw new ArgumentException($"{condition.Operator} expects at least 2 operands.");
                stack.Push(new Operation(condition.Operator, Pop(count)));
                return;
            case FilterOperator.Not:
                stack.Push(new Operation(condition.Operator, Pop(1)));
                return;
            case FilterOperator.Add or FilterOperator.Subtract or FilterOperator.Multiply or FilterOperator.Divide
                or FilterOperator.Modulo or FilterOperator.Coalesce:
                stack.Push(new Operation(condition.Operator, Pop(2)));
                return;
            case FilterOperator.Function:
                if (!Functions.TryGetValue(condition.Property, out var arity) || arity != Count())
                    throw new ArgumentException($"Unknown function '{condition.Property}' with {condition.Value} arguments.");
                stack.Push(new Operation(condition.Operator, Pop(arity), condition.Property));
                return;
            case FilterOperator.Any or FilterOperator.All or FilterOperator.Count:
                if (!simple)
                    throw new ArgumentException($"{condition.Operator} names its relation.");
                var hasInner = Count() switch
                {
                    0 => false,
                    1 => true,
                    _ => throw new ArgumentException($"{condition.Operator} has a condition or none."),
                };
                if (condition.Operator == FilterOperator.All && !hasInner)
                    throw new ArgumentException("All needs a condition.");
                stack.Push(new RelationNode(condition.Operator, condition.Property, hasInner ? Pop(1)[0] : null));
                return;
            case FilterOperator.In when !simple:
                stack.Push(new Operation(condition.Operator, Pop(1), Extra: condition.Value));
                return;
            case FilterOperator.Equal or FilterOperator.NotEqual or FilterOperator.GreaterThan or FilterOperator.GreaterThanOrEqual
                or FilterOperator.LessThan or FilterOperator.LessThanOrEqual or FilterOperator.StartsWith or FilterOperator.EndsWith
                or FilterOperator.Contains when !simple:
                stack.Push(new Operation(condition.Operator, Pop(2)));
                return;
            case FilterOperator.Equal or FilterOperator.NotEqual or FilterOperator.GreaterThan or FilterOperator.GreaterThanOrEqual
                or FilterOperator.LessThan or FilterOperator.LessThanOrEqual or FilterOperator.StartsWith or FilterOperator.EndsWith
                or FilterOperator.Contains or FilterOperator.In:
                stack.Push(new Leaf(condition));
                return;
            default:
                throw new ArgumentException($"Unknown operator: {condition.Operator}.");
        }
    }

    // ---------- Binding ----------

    /// <summary>A value whose type comes from what it is compared with (a constant sent by the client).</summary>
    private sealed class Untyped(object? value) : Expression
    {
        public object? Value { get; } = value;

        public override ExpressionType NodeType => ExpressionType.Extension;

        public override Type Type => Value?.GetType() ?? typeof(object);

        public override bool CanReduce => false;
    }

    private sealed class Binder
    {
        private const int MaxDepth = 32;

        public Expression Bind(Node node, IFilterScope scope, ParameterExpression parameter, int depth)
        {
            if (depth > MaxDepth)
                throw new ArgumentException($"A filter is limited to {MaxDepth} nested levels.");
            switch (node)
            {
                case Leaf leaf:
                    var column = Path(leaf.Condition.Property, scope, parameter, out _, out _);
                    return Filters.Build(column, leaf.Condition);

                case ValueNode value:
                    return new Untyped(value.Value);

                case MemberNode member:
                    return Path(member.Path, scope, parameter, out _, out _);

                case RelationNode relation:
                    var source = Path(relation.Path, scope, parameter, out var elementScope, out var collection);
                    if (elementScope is null || !collection)
                        throw new ArgumentException($"'{relation.Path}' is not a collection the client may filter through.");
                    LambdaExpression? inner = null;
                    if (relation.Inner is not null)
                    {
                        var element = elementScope.NewParameter();
                        inner = elementScope.Lambda(AsCondition(Bind(relation.Inner, elementScope, element, depth + 1), relation.Path), element);
                    }
                    return relation.Operator switch
                    {
                        FilterOperator.Any => elementScope.Any(source, inner),
                        FilterOperator.All => elementScope.All(source, inner!),
                        _ => elementScope.Count(source, inner),
                    };

                case Operation operation:
                    return BindOperation(operation, scope, parameter, depth);
            }
            throw new ArgumentException("Unknown node.");
        }

        private Expression BindOperation(Operation operation, IFilterScope scope, ParameterExpression parameter, int depth)
        {
            var arguments = operation.Arguments.Select(a => Bind(a, scope, parameter, depth + 1)).ToArray();
            switch (operation.Operator)
            {
                case FilterOperator.And or FilterOperator.Or:
                    var terms = arguments.Select(a => AsCondition(a, operation.Operator.ToString())).ToList();
                    return Balanced(terms, operation.Operator == FilterOperator.Or ? Expression.OrElse : Expression.AndAlso);

                case FilterOperator.Not:
                    return Expression.Not(AsCondition(arguments[0], "Not"));

                case FilterOperator.Equal or FilterOperator.NotEqual or FilterOperator.GreaterThan or FilterOperator.GreaterThanOrEqual
                    or FilterOperator.LessThan or FilterOperator.LessThanOrEqual:
                {
                    var (left, right) = Align(arguments[0], arguments[1]);
                    return Filters.Compare(left, right, operation.Operator, "the filter");
                }

                case FilterOperator.StartsWith or FilterOperator.EndsWith or FilterOperator.Contains:
                {
                    var text = Text(arguments[0]);
                    var search = Text(arguments[1]);
                    return Expression.Call(text, operation.Operator switch
                    {
                        FilterOperator.StartsWith => StringStartsWith,
                        FilterOperator.EndsWith => StringEndsWith,
                        _ => StringContains,
                    }, search);
                }

                case FilterOperator.In:
                    return Filters.In(Typed(arguments[0]), operation.Extra, "the filter");

                case FilterOperator.Coalesce:
                {
                    var left = Typed(arguments[0]);
                    var core = Nullable.GetUnderlyingType(left.Type) ?? left.Type;
                    var right = arguments[1] is Untyped untyped ? Constant(untyped.Value, core) : Convert(Typed(arguments[1]), core);
                    return Expression.Coalesce(left, right);
                }

                case FilterOperator.Add or FilterOperator.Subtract or FilterOperator.Multiply or FilterOperator.Divide or FilterOperator.Modulo:
                {
                    var (left, right) = Numbers(arguments[0], arguments[1]);
                    return operation.Operator switch
                    {
                        FilterOperator.Add => Expression.Add(left, right),
                        FilterOperator.Subtract => Expression.Subtract(left, right),
                        FilterOperator.Multiply => Expression.Multiply(left, right),
                        FilterOperator.Divide => Expression.Divide(left, right),
                        _ => Expression.Modulo(left, right),
                    };
                }

                case FilterOperator.Function:
                    return Function(operation.Name, arguments);
            }
            throw new ArgumentException($"Unknown operator: {operation.Operator}.");
        }

        private Expression Function(string name, Expression[] arguments)
        {
            switch (name)
            {
                case "lower":
                    return Expression.Call(Text(arguments[0]), ToLower);
                case "upper":
                    return Expression.Call(Text(arguments[0]), ToUpper);
                case "trim":
                    return Expression.Call(Text(arguments[0]), Trim);
                case "trimstart":
                    return Expression.Call(Text(arguments[0]), TrimStart);
                case "trimend":
                    return Expression.Call(Text(arguments[0]), TrimEnd);
                case "length":
                    return Expression.Property(Text(arguments[0]), LengthProperty);
                case "concat":
                    return Expression.Call(Concat, Text(arguments[0]), Text(arguments[1]));
                case "compare":
                    return Expression.Call(Compare, Text(arguments[0]), Text(arguments[1]));
                case "substring":
                    return Expression.Call(Text(arguments[0]), Substring2, Int(arguments[1]), Int(arguments[2]));
                case "substringfrom":
                    return Expression.Call(Text(arguments[0]), Substring1, Int(arguments[1]));
                case "abs" or "floor" or "ceiling":
                {
                    var value = Typed(arguments[0]);
                    var core = Nullable.GetUnderlyingType(value.Type) ?? value.Type;
                    if (core != typeof(decimal) && core != typeof(double))
                    {
                        if (name != "abs")
                            return value;       // an integer has no fraction
                        core = Type.GetTypeCode(core) == TypeCode.Int64 ? typeof(long) : typeof(int);
                    }
                    var method = typeof(Math).GetMethod(name switch { "abs" => nameof(Math.Abs), "floor" => nameof(Math.Floor), _ => nameof(Math.Ceiling) }, [core])
                        ?? throw new ArgumentException($"{name} does not apply to {core.Name}.");
                    return Expression.Call(method, Convert(value, core));
                }
                default:
                {
                    // Parts of a date.
                    var date = Typed(arguments[0]);
                    var core = Nullable.GetUnderlyingType(date.Type) ?? date.Type;
                    if (!IsDate(core))
                        throw new ArgumentException($"{name} applies to a date, not to {core.Name}.");
                    var value = core == date.Type ? date : Expression.Property(date, core == typeof(DateTime) ? NullableDateTime
                        : core == typeof(DateTimeOffset) ? NullableDateTimeOffset : NullableDateOnly);
                    var member = name switch
                    {
                        "year" => "Year",
                        "month" => "Month",
                        "day" => "Day",
                        "hour" => "Hour",
                        "minute" => "Minute",
                        "second" => "Second",
                        "dayofweek" => "DayOfWeek",
                        "dayofyear" => "DayOfYear",
                        _ => "Date",
                    };
                    var property = (core == typeof(DateTime) ? DateTimeParts : core == typeof(DateTimeOffset) ? DateTimeOffsetParts : DateOnlyParts)
                        .GetValueOrDefault(member) ?? throw new ArgumentException($"{name} does not apply to {core.Name}.");
                    return Expression.Property(value, property);
                }
            }
        }

        private Expression Text(Expression value)
        {
            var typed = value is Untyped untyped ? Constant(untyped.Value?.ToString(), typeof(string)) : value;
            return typed.Type == typeof(string) ? typed : throw new ArgumentException($"A text is expected, not {typed.Type.Name}.");
        }

        private Expression Int(Expression value) => value is Untyped untyped ? Constant(untyped.Value, typeof(int)) : Convert(value, typeof(int));

        public Expression Typed(Expression value) => value is Untyped untyped
            ? untyped.Value is null ? Expression.Constant(null, typeof(object)) : Expression.Constant(untyped.Value, untyped.Value.GetType())
            : value;

        /// <summary>Two sides of a comparison, of one type: a constant takes the type of the other side.</summary>
        private (Expression Left, Expression Right) Align(Expression left, Expression right)
        {
            if (left is Untyped a && right is Untyped b)
                return (Typed(a), Typed(b));
            if (left is Untyped constantLeft)
                return (Constant(constantLeft.Value, right.Type), right);
            if (right is Untyped constantRight)
                return (left, Constant(constantRight.Value, left.Type));
            if (left.Type == right.Type)
                return (left, right);
            if (IsNumber(left.Type) && IsNumber(right.Type))
                return Numbers(left, right);
            // int and int?: both nullable.
            if (Nullable.GetUnderlyingType(left.Type) == right.Type)
                return (left, Expression.Convert(right, left.Type));
            if (Nullable.GetUnderlyingType(right.Type) == left.Type)
                return (Expression.Convert(left, right.Type), right);
            return (left, right);
        }

        /// <summary>Two numbers in a common type: decimal, double, long or int, nullable when one is.</summary>
        private (Expression Left, Expression Right) Numbers(Expression left, Expression right)
        {
            if (left is Untyped a && right is not Untyped)
                left = Constant(a.Value, Nullable.GetUnderlyingType(right.Type) ?? right.Type);
            else if (right is Untyped b && left is not Untyped)
                right = Constant(b.Value, Nullable.GetUnderlyingType(left.Type) ?? left.Type);
            left = Typed(left);
            right = Typed(right);
            if (!IsNumber(left.Type) || !IsNumber(right.Type))
                throw new ArgumentException($"A computation on {left.Type.Name} and {right.Type.Name} is not possible.");
            var (l, r) = (Core(left.Type), Core(right.Type));
            var common = l == typeof(decimal) || r == typeof(decimal) ? typeof(decimal)
                : l == typeof(double) || r == typeof(double) || l == typeof(float) || r == typeof(float) ? typeof(double)
                : l == typeof(long) || r == typeof(long) ? typeof(long)
                : typeof(int);
            var nullable = Nullable.GetUnderlyingType(left.Type) is not null || Nullable.GetUnderlyingType(right.Type) is not null;
            var target = nullable ? NullableOf(common) : common;
            return (Convert(left, target), Convert(right, target));
        }

        private static Type Core(Type type)
        {
            var core = Nullable.GetUnderlyingType(type) ?? type;
            return core.IsEnum ? Enum.GetUnderlyingType(core) : core;
        }

        private static bool IsNumber(Type type) => Type.GetTypeCode(Core(type)) is TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64
            or TypeCode.Byte or TypeCode.SByte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64 or TypeCode.Decimal or TypeCode.Double or TypeCode.Single;

        private static Type NullableOf(Type type) => type == typeof(decimal) ? typeof(decimal?)
            : type == typeof(double) ? typeof(double?) : type == typeof(long) ? typeof(long?) : typeof(int?);

        private static Expression Convert(Expression value, Type type) => value.Type == type ? value : Expression.Convert(value, type);

        private static ConstantExpression Constant(object? value, Type type)
            => Expression.Constant(Filters.ConvertTo(value, type, "the filter"), type);

        /// <summary>
        /// A path from the parameter: properties of its type, then through navigations to the properties of other exposed
        /// types. Only what the client may filter on is found; anything else is "unknown".
        /// </summary>
        private static Expression Path(string path, IFilterScope scope, ParameterExpression parameter, out IFilterScope? last, out bool collection)
        {
            var names = path.Split('.');
            if (names.Length > 4 || names.Any(string.IsNullOrEmpty))
                throw new ArgumentException($"Unknown property '{path}'.");
            Expression current = parameter;
            last = null;
            collection = false;
            for (var i = 0; i < names.Length; i++)
            {
                var isLast = i == names.Length - 1;
                if (isLast && scope.Column(names[i]) is { } column)
                    return Expression.Property(current, column);
                if (scope.Navigation(names[i]) is not { } navigation || !isLast && navigation.Collection)
                    throw new ArgumentException($"Unknown property '{path}' on {scope.Type.Name}.");
                current = Expression.Property(current, navigation.Property);
                last = navigation.Scope;
                collection = navigation.Collection;
                scope = navigation.Scope;
            }
            return current;
        }
    }

    private static Expression AsCondition(Expression expression, string where)
    {
        if (expression is Untyped { Value: bool flag })
            return Expression.Constant(flag);
        if (expression.Type == typeof(bool))
            return expression;
        if (expression.Type == typeof(bool?))
            return Expression.Equal(expression, Expression.Constant(true, typeof(bool?)));
        throw new ArgumentException($"{where}: a condition is expected.");
    }

    private static Expression Balanced(IReadOnlyList<Expression> terms, Func<Expression, Expression, BinaryExpression> combine)
    {
        if (terms.Count == 1)
            return terms[0];
        var middle = terms.Count / 2;
        return combine(Balanced(terms.Take(middle).ToList(), combine), Balanced(terms.Skip(middle).ToList(), combine));
    }

    // ---------- Members and helpers ----------

    private static readonly MethodInfo StringStartsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo StringEndsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;
    private static readonly MethodInfo StringContains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
    private static readonly MethodInfo ToLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
    private static readonly MethodInfo ToUpper = typeof(string).GetMethod(nameof(string.ToUpper), Type.EmptyTypes)!;
    private static readonly MethodInfo Trim = typeof(string).GetMethod(nameof(string.Trim), Type.EmptyTypes)!;
    private static readonly MethodInfo TrimStart = typeof(string).GetMethod(nameof(string.TrimStart), Type.EmptyTypes)!;
    private static readonly MethodInfo TrimEnd = typeof(string).GetMethod(nameof(string.TrimEnd), Type.EmptyTypes)!;
    private static readonly MethodInfo Substring1 = typeof(string).GetMethod(nameof(string.Substring), [typeof(int)])!;
    private static readonly MethodInfo Substring2 = typeof(string).GetMethod(nameof(string.Substring), [typeof(int), typeof(int)])!;
    private static readonly MethodInfo Concat = typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!;
    private static readonly MethodInfo Compare = typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;
    private static readonly PropertyInfo NullableDateTime = typeof(DateTime?).GetProperty(nameof(Nullable<DateTime>.Value))!;
    private static readonly PropertyInfo NullableDateTimeOffset = typeof(DateTimeOffset?).GetProperty(nameof(Nullable<DateTimeOffset>.Value))!;
    private static readonly PropertyInfo NullableDateOnly = typeof(DateOnly?).GetProperty(nameof(Nullable<DateOnly>.Value))!;
    private static readonly PropertyInfo LengthProperty = typeof(string).GetProperty(nameof(string.Length))!;

    private static readonly string[] Parts = ["Year", "Month", "Day", "Hour", "Minute", "Second", "DayOfWeek", "DayOfYear", "Date"];
    private static readonly Dictionary<string, PropertyInfo> DateTimeParts = Parts.ToDictionary(p => p, p => typeof(DateTime).GetProperty(p)!);
    private static readonly Dictionary<string, PropertyInfo> DateTimeOffsetParts = Parts.ToDictionary(p => p, p => typeof(DateTimeOffset).GetProperty(p)!);
    private static readonly Dictionary<string, PropertyInfo> DateOnlyParts = new[] { "Year", "Month", "Day", "DayOfWeek", "DayOfYear" }
        .ToDictionary(p => p, p => typeof(DateOnly).GetProperty(p)!);

    private static FilterOperator? Comparison(ExpressionType type) => type switch
    {
        ExpressionType.Equal => FilterOperator.Equal,
        ExpressionType.NotEqual => FilterOperator.NotEqual,
        ExpressionType.GreaterThan => FilterOperator.GreaterThan,
        ExpressionType.GreaterThanOrEqual => FilterOperator.GreaterThanOrEqual,
        ExpressionType.LessThan => FilterOperator.LessThan,
        ExpressionType.LessThanOrEqual => FilterOperator.LessThanOrEqual,
        _ => null,
    };

    private static FilterOperator? Arithmetic(ExpressionType type) => type switch
    {
        ExpressionType.Add or ExpressionType.AddChecked => FilterOperator.Add,
        ExpressionType.Subtract or ExpressionType.SubtractChecked => FilterOperator.Subtract,
        ExpressionType.Multiply or ExpressionType.MultiplyChecked => FilterOperator.Multiply,
        ExpressionType.Divide => FilterOperator.Divide,
        ExpressionType.Modulo => FilterOperator.Modulo,
        _ => null,
    };

    private static bool IsDate(Type type)
    {
        var core = Nullable.GetUnderlyingType(type) ?? type;
        return core == typeof(DateTime) || core == typeof(DateTimeOffset) || core == typeof(DateOnly);
    }

    private static Expression StripConvert(Expression expression, bool keepBool)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
               && !(keepBool && convert.Type == typeof(bool)))
            expression = convert.Operand;
        return expression;
    }

    private static Expression StripQuote(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : expression;

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

    private static object? Evaluate(Expression expression)
        => CapturedValues.TryEvaluate(expression, out var value)
            ? value
            : Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object))).Compile(preferInterpretation: true)();

    internal static object? Normalize(object? value) => value switch
    {
        null => null,
        Enum e => System.Convert.ToInt64(e, CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        char c => c.ToString(),
        DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("O", CultureInfo.InvariantCulture),
        _ => value,
    };

    private static NotSupportedException Unsupported(string reason)
        => new($"Predicate cannot be sent to the server: {reason}. Supported: comparisons of properties, values and computations " +
               "on them (arithmetic, ToLower, ToUpper, Trim, Length, Substring, date parts, Math.Abs, Floor, Ceiling, ??, " +
               "string.Compare), StartsWith/EndsWith/Contains, list.Contains(e.Prop), Any/All/Count on a relation, a property of a " +
               "navigation, combined with &&, || and !.");

    private sealed class ParameterFinder(IReadOnlySet<ParameterExpression> parameters) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (parameters.Contains(node))
                Found = true;
            return node;
        }
    }
}

/// <summary>
/// The properties of one entity type a client may filter on, and its navigations toward other exposed types: what the
/// server binds the names of a filter to. A handler gives the scope of its type.
/// </summary>
internal interface IFilterScope
{
    Type Type { get; }

    ParameterExpression NewParameter();

    /// <summary>A column the client may filter on; null otherwise.</summary>
    PropertyInfo? Column(string name);

    /// <summary>A navigation (one entity, or a collection) toward a type the server exposes; null otherwise.</summary>
    (PropertyInfo Property, IFilterScope Scope, bool Collection)? Navigation(string name);

    /// <summary><c>x =&gt; body</c>, typed for this entity type.</summary>
    LambdaExpression Lambda(Expression body, ParameterExpression parameter);

    Expression Any(Expression source, LambdaExpression? predicate);

    Expression All(Expression source, LambdaExpression predicate);

    Expression Count(Expression source, LambdaExpression? predicate);
}

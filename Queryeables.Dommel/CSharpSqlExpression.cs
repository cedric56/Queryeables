using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Dommel;
using Queryeables.Abstraction;

namespace Queryeables.Dommel;

/// <summary>
/// The translation of a predicate into SQL, made to give the rows C# gives: the result of a filter must not depend on whether
/// it runs in the database, in a cache, or in the offline copy. It replaces Dommel's translation of the WHERE clause.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Comparisons between a column and a value, between two columns, or of values computed from columns: arithmetic
/// (<c>+ - * / %</c>), text (<c>ToLower</c>, <c>ToUpper</c>, <c>Trim</c>, <c>Length</c>, <c>Substring</c>, concatenation),
/// dates (<c>Year</c>, <c>Month</c>, <c>Day</c>, <c>Hour</c>, <c>Minute</c>, <c>Second</c>, <c>DayOfWeek</c>,
/// <c>DayOfYear</c>, <c>Date</c>), <c>Math.Abs</c>, <c>Math.Floor</c>, <c>Math.Ceiling</c>, <c>??</c>, and
/// <c>string.Compare(a, b) &gt; 0</c> (text compared as the collation does).</item>
/// <item><c>Name.Contains("50%")</c>, <c>StartsWith</c>, <c>EndsWith</c>: <c>%</c>, <c>_</c> and <c>[</c> are text, not
/// wildcards (LIKE ... ESCAPE). With PostgreSQL, LIKE instead of ILIKE; with SQLite, <c>instr</c> and <c>substr</c>: both
/// compare case-sensitively, as C# does. SQL Server and MySQL compare as their collation says (case-insensitive by default).</item>
/// <item>Nulls as in C#: <c>x != v</c> keeps the rows where x is null, <c>a == b</c> holds when both are null,
/// <c>!(x &gt; v)</c> keeps the rows where x is null.</item>
/// <item><c>list.Contains(e.Id)</c> on a variable or a constant list (an empty list keeps no row).</item>
/// </list>
/// </remarks>
internal sealed class CSharpSqlExpression<TEntity>(ISqlBuilder sqlBuilder) : SqlExpression<TEntity>(sqlBuilder)
{
    private const char Escape = '!';

    protected override object VisitLambda(LambdaExpression expression)
        => new Sql(this, expression.Parameters[0]).Condition(expression.Body);

    /// <summary>The SQL of a value computed from the entity (a key, the value of an aggregate); its parameters join this expression's.</summary>
    public string ValueSql(LambdaExpression value) => new Sql(this, value.Parameters[0]).Value(value.Body);

    /// <summary>The SQL of an aggregate over <paramref name="value"/> (count(*) without one), as C# computes it.</summary>
    public string AggregateSql(AggregateFunction function, LambdaExpression? value, Type? type)
    {
        if (function == AggregateFunction.Count || value is null)
            return "count(*)";
        var sql = ValueSql(value);
        var core = Nullable.GetUnderlyingType(type ?? typeof(object)) ?? type ?? typeof(object);
        // SQLite keeps decimals as text: compared and summed as numbers. Averages of integers keep their fraction.
        if (core == typeof(decimal) && Kind == Dialect.Sqlite)
            sql = $"cast({sql} as real)";
        else if (function == AggregateFunction.Average && core != typeof(decimal))
            sql = $"cast({sql} as {(Kind == Dialect.Postgres ? "double precision" : Kind == Dialect.MySql ? "double" : "float")})";
        return function switch
        {
            AggregateFunction.Sum => $"sum({sql})",
            AggregateFunction.Min => $"min({sql})",
            AggregateFunction.Max => $"max({sql})",
            _ => $"avg({sql})",
        };
    }

    private enum Dialect
    {
        Sqlite,
        Postgres,
        SqlServer,
        MySql,
    }

    private Dialect Kind => SqlBuilder switch
    {
        SqliteSqlBuilder => Dialect.Sqlite,
        PostgresSqlBuilder => Dialect.Postgres,
        MySqlSqlBuilder => Dialect.MySql,
        _ => Dialect.SqlServer,
    };

    private string Column(MemberExpression member) => MemberToColumn(member);

    private string Parameter(object? value)
    {
        if (value is null)
            return "null";
        AddParameter(value, out var name);
        return name;
    }

    /// <summary>The SQL of one predicate: conditions (true, false) and values (a column, a parameter, a computation).</summary>
    private sealed class Sql(CSharpSqlExpression<TEntity> owner, ParameterExpression parameter)
    {
        private Dialect Kind { get; } = owner.Kind;

        // ---------- Conditions ----------

        public string Condition(Expression expression)
        {
            switch (expression)
            {
                case BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } logical:
                    var op = logical.NodeType == ExpressionType.AndAlso ? "and" : "or";
                    return $"({Condition(logical.Left)} {op} {Condition(logical.Right)})";

                case UnaryExpression { NodeType: ExpressionType.Not } not:
                    return Not(not.Operand);

                case BinaryExpression binary when IsComparison(binary.NodeType):
                    return Comparison(binary.NodeType, binary.Left, binary.Right);

                case MethodCallExpression call:
                    return Call(call);

                case UnaryExpression { NodeType: ExpressionType.Convert } convert when convert.Type == typeof(bool):
                    return Condition(convert.Operand);
            }

            if (!Uses(expression))
                return Evaluate(expression) is true ? "1 = 1" : "1 = 0";
            if (expression.Type == typeof(bool) || expression.Type == typeof(bool?))
                return $"{Value(expression)} = {owner.Parameter(true)}";        // a flag: e.Active
            throw Unsupported(expression);
        }

        /// <summary>Not, as C# reads it: a comparison with a null is false, so its negation is true.</summary>
        private string Not(Expression operand)
        {
            operand = StripBool(operand);
            switch (operand)
            {
                case BinaryExpression { NodeType: ExpressionType.AndAlso } and:
                    return $"({Not(and.Left)} or {Not(and.Right)})";
                case BinaryExpression { NodeType: ExpressionType.OrElse } or:
                    return $"({Not(or.Left)} and {Not(or.Right)})";
                case UnaryExpression { NodeType: ExpressionType.Not } not:
                    return Condition(not.Operand);
                case BinaryExpression binary when IsComparison(binary.NodeType):
                    if (binary.NodeType is ExpressionType.Equal or ExpressionType.NotEqual)
                        return Comparison(binary.NodeType == ExpressionType.Equal ? ExpressionType.NotEqual : ExpressionType.Equal, binary.Left, binary.Right);
                    var nulls = NullTests(binary.Left, binary.Right);
                    var negated = Comparison(Negate(binary.NodeType), binary.Left, binary.Right);
                    return nulls.Length == 0 ? negated : $"({negated} or {nulls})";
            }
            if (!Uses(operand))
                return Evaluate(operand) is true ? "1 = 0" : "1 = 1";
            if (operand is MemberExpression && (operand.Type == typeof(bool)))
                return $"{Value(operand)} = {owner.Parameter(false)}";
            // !list.Contains(e.X): a null X is not in the list, as C# says.
            if (operand is MethodCallExpression { Method.Name: "Contains" } call && In(call) is { } list)
            {
                var item = call.Object is null ? call.Arguments[1] : call.Arguments[0];
                return MayBeNull(Strip(item)) ? $"(not ({list}) or {Value(item)} is null)" : $"not ({list})";
            }
            return $"not ({Condition(operand)})";
        }

        private string NullTests(Expression left, Expression right)
        {
            var tests = new[] { left, right }.Where(e => Uses(e) && MayBeNull(e)).Select(e => $"{Value(e)} is null").ToArray();
            return string.Join(" or ", tests);
        }

        private string Comparison(ExpressionType type, Expression left, Expression right)
        {
            left = Strip(left);
            right = Strip(right);

            // string.Compare(a, b) > 0, a.CompareTo(b) <= 0: text compared by the database.
            if (Compared(left) is { } pair && IsZero(right))
                return Comparison(type, pair.Left, pair.Right);
            if (Compared(right) is { } mirrored && IsZero(left))
                return Comparison(Mirror(type), mirrored.Left, mirrored.Right);

            var leftNull = IsNullConstant(left);
            var rightNull = IsNullConstant(right);
            if (leftNull || rightNull)
            {
                if (leftNull && rightNull)
                    return type == ExpressionType.Equal ? "1 = 1" : "1 = 0";
                var value = Value(leftNull ? right : left);
                return type switch
                {
                    ExpressionType.Equal => $"{value} is null",
                    ExpressionType.NotEqual => $"{value} is not null",
                    _ => "1 = 0",                                       // an ordering with null is false in C#
                };
            }

            var (a, b) = (Value(left), Value(right));
            var op = type switch
            {
                ExpressionType.Equal => "=",
                ExpressionType.NotEqual => "<>",
                ExpressionType.GreaterThan => ">",
                ExpressionType.GreaterThanOrEqual => ">=",
                ExpressionType.LessThan => "<",
                _ => "<=",
            };
            var (leftMay, rightMay) = (Uses(left) && MayBeNull(left), Uses(right) && MayBeNull(right));
            return type switch
            {
                // null == null is true in C#, null <> 5 is true: SQL says unknown for both.
                ExpressionType.Equal when leftMay && rightMay => $"({a} = {b} or ({a} is null and {b} is null))",
                ExpressionType.NotEqual when leftMay && rightMay => $"({a} <> {b} or ({a} is null and {b} is not null) or ({a} is not null and {b} is null))",
                ExpressionType.NotEqual when leftMay => $"({a} <> {b} or {a} is null)",
                ExpressionType.NotEqual when rightMay => $"({a} <> {b} or {b} is null)",
                _ => $"{a} {op} {b}",
            };
        }

        private string Call(MethodCallExpression call)
        {
            var method = call.Method;
            if (method.DeclaringType == typeof(string) && !method.IsStatic && call.Arguments.Count == 1 && call.Arguments[0].Type == typeof(string)
                && method.Name is nameof(string.StartsWith) or nameof(string.EndsWith) or nameof(string.Contains))
                return TextSearch(method.Name, call.Object!, call.Arguments[0]);

            if (method.Name == nameof(string.IsNullOrEmpty) && method.DeclaringType == typeof(string) && call.Arguments.Count == 1)
            {
                var text = Value(call.Arguments[0]);
                return $"({text} is null or {text} = '')";
            }

            if (method.Name == nameof(Equals) && call.Arguments.Count == 1 && call.Object is not null)
                return Comparison(ExpressionType.Equal, call.Object, call.Arguments[0]);

            if (method.Name == "Contains" && In(call) is { } list)
                return list;

            if (!Uses(call))
                return Evaluate(call) is true ? "1 = 1" : "1 = 0";
            throw Unsupported(call);
        }

        /// <summary><c>list.Contains(e.Id)</c>: <c>id in (...)</c>, its nulls as C# finds them.</summary>
        private string? In(MethodCallExpression call)
        {
            var (source, item) = call.Object is null && (call.Arguments.Count == 2 || call.Arguments.Count == 3 && call.Arguments[2] is ConstantExpression { Value: null })
                ? (call.Arguments[0], call.Arguments[1])
                : (call.Object, call.Arguments.Count == 1 ? call.Arguments[0] : null);
            if (source is null || item is null || source.Type == typeof(string) || Uses(source) || !Uses(item))
                return null;
            if (Evaluate(UnwrapSpan(source)) is not IEnumerable values)
                throw Unsupported(call);

            var column = Value(item);
            var parameters = new List<string>();
            var hasNull = false;
            foreach (var value in values)
            {
                if (value is null)
                    hasNull = true;
                else
                    parameters.Add(owner.Parameter(value));
            }
            var list = parameters.Count == 0 ? "1 = 0" : $"{column} in ({string.Join(", ", parameters)})";
            return hasNull ? $"({list} or {column} is null)" : list;
        }

        private string TextSearch(string method, Expression text, Expression search)
        {
            var column = Value(text);
            if (Uses(search))
            {
                // Two columns: the functions of each database, without wildcards.
                var other = Value(search);
                return (method, Kind) switch
                {
                    (nameof(string.Contains), Dialect.SqlServer) => $"charindex({other}, {column}) > 0",
                    (nameof(string.Contains), Dialect.Postgres) => $"strpos({column}, {other}) > 0",
                    (nameof(string.Contains), _) => $"instr({column}, {other}) > 0",
                    (nameof(string.StartsWith), _) => $"{Substring(column, "1", Length(other))} = {other}",
                    _ => $"({Length(column)} >= {Length(other)} and {Substring(column, $"{Length(column)} - {Length(other)} + 1", Length(other))} = {other})",
                };
            }

            var textValue = Evaluate(search)?.ToString() ?? string.Empty;
            if (Kind == Dialect.Sqlite)
            {
                // SQLite's LIKE ignores the case of ASCII letters: instr and substr compare exactly.
                var value = owner.Parameter(textValue);
                return method switch
                {
                    nameof(string.StartsWith) => $"substr({column}, 1, length({value})) = {value}",
                    nameof(string.EndsWith) => $"(length({column}) >= length({value}) and substr({column}, length({column}) - length({value}) + 1) = {value})",
                    _ => $"instr({column}, {value}) > 0",
                };
            }

            var escaped = textValue
                .Replace(Escape.ToString(), $"{Escape}{Escape}", StringComparison.Ordinal)
                .Replace("%", $"{Escape}%", StringComparison.Ordinal)
                .Replace("_", $"{Escape}_", StringComparison.Ordinal)
                .Replace("[", $"{Escape}[", StringComparison.Ordinal);      // a wildcard of SQL Server
            var pattern = method switch
            {
                nameof(string.StartsWith) => $"{escaped}%",
                nameof(string.EndsWith) => $"%{escaped}",
                _ => $"%{escaped}%",
            };
            return $"{column} like {owner.Parameter(pattern)} escape '{Escape}'";
        }

        // ---------- Values ----------

        public string Value(Expression expression)
        {
            expression = Strip(expression);
            if (!Uses(expression))
                return owner.Parameter(Normalize(Evaluate(expression)));

            switch (expression)
            {
                case MemberExpression member when member.Expression == parameter:
                    return owner.Column(member);

                case MemberExpression { Member.Name: "Value", Expression: { } nullable } when Nullable.GetUnderlyingType(nullable.Type) is not null:
                    return Value(nullable);

                case MemberExpression { Member.Name: "Length", Expression: { } text } when text.Type == typeof(string):
                    return Length(Value(text));

                case MemberExpression { Expression: { } date } member when IsDate(date.Type):
                    return DatePart(member.Member.Name, Value(date), member);

                case BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce:
                    return $"coalesce({Value(coalesce.Left)}, {Value(coalesce.Right)})";

                case BinaryExpression { NodeType: ExpressionType.Add } concat when concat.Type == typeof(string):
                    return Concat(Value(concat.Left), Value(concat.Right));

                case BinaryExpression arithmetic when Arithmetic(arithmetic.NodeType) is { } op:
                    if (op == "/" && Kind == Dialect.MySql && IsInteger(arithmetic.Type))
                        op = "div";                                     // C# divides integers without a remainder
                    return $"({Value(arithmetic.Left)} {op} {Value(arithmetic.Right)})";

                case MethodCallExpression call:
                    return Function(call);

                case ConditionalExpression conditional:
                    return $"(case when {Condition(conditional.Test)} then {Value(conditional.IfTrue)} else {Value(conditional.IfFalse)} end)";

                default:
                    if (expression.Type == typeof(bool))
                        return $"(case when {Condition(expression)} then 1 else 0 end)";
                    throw Unsupported(expression);
            }
        }

        private string Function(MethodCallExpression call)
        {
            var method = call.Method;
            var name = method.Name;
            if (method.DeclaringType == typeof(string) && call.Object is { } text)
            {
                var value = Value(text);
                switch (name)
                {
                    case nameof(string.ToLower) or nameof(string.ToLowerInvariant) when call.Arguments.Count == 0:
                        return $"lower({value})";
                    case nameof(string.ToUpper) or nameof(string.ToUpperInvariant) when call.Arguments.Count == 0:
                        return $"upper({value})";
                    case nameof(string.Trim) when call.Arguments.Count == 0:
                        return Kind == Dialect.SqlServer ? $"ltrim(rtrim({value}))" : $"trim({value})";
                    case nameof(string.TrimStart) when call.Arguments.Count == 0:
                        return $"ltrim({value})";
                    case nameof(string.TrimEnd) when call.Arguments.Count == 0:
                        return $"rtrim({value})";
                    case nameof(string.Substring) when call.Arguments.Count == 2:
                        return Substring(value, $"{Value(call.Arguments[0])} + 1", Value(call.Arguments[1]));
                    case nameof(string.Substring) when call.Arguments.Count == 1:
                        return Substring(value, $"{Value(call.Arguments[0])} + 1", Length(value));
                }
            }
            if (method.DeclaringType == typeof(string) && name == nameof(string.Concat) && call.Arguments.All(a => a.Type == typeof(string)))
                return call.Arguments.Select(Value).Aggregate(Concat);
            if (method.DeclaringType == typeof(Math) && call.Arguments.Count == 1)
            {
                var value = Value(call.Arguments[0]);
                switch (name)
                {
                    case nameof(Math.Abs):
                        return $"abs({value})";
                    case nameof(Math.Floor):
                        return $"floor({value})";
                    case nameof(Math.Ceiling):
                        return Kind == Dialect.Sqlite ? $"(-floor(-({value})))" : $"ceiling({value})";
                }
            }
            if (method.Name is nameof(string.Compare) or nameof(string.CompareTo))
                throw new NotSupportedException($"'{call}': compare the result with 0 (string.Compare(a, b) > 0).");
            throw Unsupported(call);
        }

        private string DatePart(string part, string date, MemberExpression member)
        {
            // A part of a DateTimeOffset depends on its offset in C#, and on the session's time zone or UTC in SQL (when the
            // database keeps no offset): the same filter would select other rows. Refused rather than answer differently.
            if (Nullable.GetUnderlyingType(member.Expression!.Type) is { } nullable ? nullable == typeof(DateTimeOffset) : member.Expression.Type == typeof(DateTimeOffset))
                throw new NotSupportedException(
                    $"'{member}': the parts of a DateTimeOffset are not translated (their value depends on the offset, which the " +
                    "database may not keep). Compare the whole value with a bound instead (e.Date >= from && e.Date < to).");
            var sqlite = part switch
            {
                "Year" => "%Y",
                "Month" => "%m",
                "Day" => "%d",
                "Hour" => "%H",
                "Minute" => "%M",
                "Second" => "%S",
                "DayOfWeek" => "%w",
                "DayOfYear" => "%j",
                _ => null,
            };
            return (part, Kind) switch
            {
                ("Date", Dialect.Sqlite) => $"datetime({date}, 'start of day')",
                ("Date", Dialect.Postgres) => $"date_trunc('day', {date})",
                ("Date", Dialect.MySql) => $"cast(date({date}) as datetime)",
                ("Date", _) => $"cast(cast({date} as date) as datetime2)",
                (_, Dialect.Sqlite) when sqlite is not null => $"cast(strftime('{sqlite}', {date}) as integer)",
                ("DayOfWeek", Dialect.Postgres) => $"cast(extract(dow from {date}) as integer)",
                ("DayOfYear", Dialect.Postgres) => $"cast(extract(doy from {date}) as integer)",
                ("Second", Dialect.Postgres) => $"cast(floor(extract(second from {date})) as integer)",
                (_, Dialect.Postgres) when sqlite is not null => $"cast(extract({part.ToLowerInvariant()} from {date}) as integer)",
                ("DayOfWeek", Dialect.MySql) => $"(dayofweek({date}) - 1)",
                (_, Dialect.MySql) when sqlite is not null => $"{part.ToLowerInvariant()}({date})",
                ("DayOfWeek", _) => $"((datepart(weekday, {date}) + @@datefirst - 1) % 7)",
                (_, _) when sqlite is not null => $"datepart({part.ToLowerInvariant()}, {date})",
                _ => throw Unsupported(member),
            };
        }

        private string Length(string value) => Kind switch
        {
            Dialect.SqlServer => $"(len({value} + 'x') - 1)",           // len ignores the trailing spaces C# counts
            Dialect.MySql => $"char_length({value})",
            _ => $"length({value})",
        };

        private string Substring(string value, string start, string length)
            => Kind is Dialect.SqlServer or Dialect.MySql ? $"substring({value}, {start}, {length})" : $"substr({value}, {start}, {length})";

        /// <summary>a + b on text: a null is empty in C#.</summary>
        private string Concat(string a, string b) => Kind switch
        {
            Dialect.SqlServer => $"(coalesce({a}, '') + coalesce({b}, ''))",
            Dialect.MySql => $"concat(coalesce({a}, ''), coalesce({b}, ''))",
            _ => $"(coalesce({a}, '') || coalesce({b}, ''))",
        };

        // ---------- Helpers ----------

        private bool Uses(Expression expression)
        {
            var finder = new ParameterFinder(parameter);
            finder.Visit(expression);
            return finder.Found;
        }

        private static object? Evaluate(Expression expression)
            => CapturedValues.TryEvaluate(expression, out var value)
                ? value
                : Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object))).Compile(preferInterpretation: true)();

        /// <summary>An enum is compared as the number the column holds.</summary>
        private static object? Normalize(object? value) => value is Enum e ? Convert.ToInt64(e, CultureInfo.InvariantCulture) : value;

        private static Expression Strip(Expression expression)
        {
            while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
                   && convert.Type != typeof(bool))
                expression = convert.Operand;
            return expression;
        }

        private static Expression StripBool(Expression expression)
            => expression is UnaryExpression { NodeType: ExpressionType.Convert } convert && convert.Type == typeof(bool) ? convert.Operand : expression;

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

        private static bool MayBeNull(Expression expression)
            => !expression.Type.IsValueType || Nullable.GetUnderlyingType(expression.Type) is not null;

        /// <summary>
        /// A null, written or computed without the entity (a method call, a ternary on variables): compared as C# compares
        /// with null (<c>is null</c>), never as a parameter (<c>= null</c> keeps no row).
        /// </summary>
        private bool IsNullConstant(Expression expression)
            => expression is ConstantExpression { Value: null } || !Uses(expression) && Evaluate(expression) is null;

        private static bool IsZero(Expression expression)
            => expression is ConstantExpression { Value: int zero } && zero == 0;

        /// <summary>string.Compare(a, b) or a.CompareTo(b): the two texts compared.</summary>
        private static (Expression Left, Expression Right)? Compared(Expression expression) => expression switch
        {
            MethodCallExpression { Method.Name: nameof(string.Compare), Object: null, Arguments.Count: 2 } compare
                when compare.Method.DeclaringType == typeof(string) => (compare.Arguments[0], compare.Arguments[1]),
            MethodCallExpression { Method.Name: nameof(string.CompareTo), Object: { } self, Arguments.Count: 1 } compareTo
                when self.Type == typeof(string) => (self, compareTo.Arguments[0]),
            _ => null,
        };

        private static bool IsComparison(ExpressionType type) => type is ExpressionType.Equal or ExpressionType.NotEqual
            or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

        private static ExpressionType Negate(ExpressionType type) => type switch
        {
            ExpressionType.GreaterThan => ExpressionType.LessThanOrEqual,
            ExpressionType.GreaterThanOrEqual => ExpressionType.LessThan,
            ExpressionType.LessThan => ExpressionType.GreaterThanOrEqual,
            _ => ExpressionType.GreaterThan,
        };

        private static ExpressionType Mirror(ExpressionType type) => type switch
        {
            ExpressionType.GreaterThan => ExpressionType.LessThan,
            ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
            ExpressionType.LessThan => ExpressionType.GreaterThan,
            ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
            _ => type,
        };

        private static string? Arithmetic(ExpressionType type) => type switch
        {
            ExpressionType.Add or ExpressionType.AddChecked => "+",
            ExpressionType.Subtract or ExpressionType.SubtractChecked => "-",
            ExpressionType.Multiply or ExpressionType.MultiplyChecked => "*",
            ExpressionType.Divide => "/",
            ExpressionType.Modulo => "%",
            _ => null,
        };

        private static bool IsInteger(Type type) => Type.GetTypeCode(Nullable.GetUnderlyingType(type) ?? type)
            is TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or TypeCode.Byte or TypeCode.SByte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64;

        private static bool IsDate(Type type)
        {
            var core = Nullable.GetUnderlyingType(type) ?? type;
            return core == typeof(DateTime) || core == typeof(DateTimeOffset) || core == typeof(DateOnly);
        }

        private static NotSupportedException Unsupported(Expression expression)
            => new($"'{expression}' cannot be translated into SQL. Supported: comparisons of columns, values and computations on them " +
                   "(arithmetic, ToLower, ToUpper, Trim, Length, Substring, date parts, Math.Abs, Floor, Ceiling, ??), " +
                   "StartsWith, EndsWith, Contains, list.Contains(e.Column), string.Compare(a, b) compared with 0, &&, || and !.");

        private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor
        {
            public bool Found { get; private set; }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (node == parameter)
                    Found = true;
                return node;
            }
        }
    }
}

/// <summary>Installs <see cref="CSharpSqlExpression{TEntity}"/> as Dommel's translation, for the entity types of Queryeables.</summary>
internal static class CSharpSql
{
    private static readonly ConcurrentDictionary<Type, Func<ISqlBuilder, object>> Factories = new();
    private static readonly Func<Type, ISqlBuilder, object> Previous;

    static CSharpSql()
    {
        // Dommel's factory is global: the types a Queryeables store reads get the C# translation, the others keep theirs.
        Previous = DommelMapper.SqlExpressionFactory;
        DommelMapper.SqlExpressionFactory = (type, builder) => Factories.TryGetValue(type, out var create) ? create(builder) : Previous(type, builder);
    }

    /// <summary>Called before each translation of <typeparamref name="TEntity"/>: no reflection, so Native AOT is fine.</summary>
    public static void Use<[DynamicallyAccessedMembers(Abstraction.EntityMembers.Required)] TEntity>() where TEntity : class
        => Factories.TryAdd(typeof(TEntity), static builder => new CSharpSqlExpression<TEntity>(builder));
}

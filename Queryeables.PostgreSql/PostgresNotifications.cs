using Queryeables.Abstraction;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Queryeables.PostgreSql;

/// <summary>
/// The tables whose changes PostgreSQL reports to the change feeds, through triggers and <c>LISTEN/NOTIFY</c>
/// (see <see cref="PostgresChangeListener"/>).
/// </summary>
/// <example>
/// <code>
/// q.UseNpgsql(connectionString, notifications: n => n
///     .Watch&lt;Person&gt;(p => p.Id)
///     .Watch&lt;Contact&gt;(c => c.Id))
/// </code>
/// </example>
public sealed class PostgresNotifications
{
    /// <summary>The channel the triggers notify, and the listener listens to.</summary>
    public const string Channel = "queryeables_changes";

    /// <summary>The trigger function, shared by every watched table.</summary>
    public const string FunctionName = "queryeables_notify";

    private readonly List<WatchedTable> _tables = [];

    /// <summary>The watched tables, with the column that holds the key sent in each notification.</summary>
    public IReadOnlyList<WatchedTable> Tables => _tables;

    /// <summary>
    /// Creates the trigger function and the triggers when the listener starts (<c>CREATE OR REPLACE</c>, idempotent).
    /// Turn it off when the database user may not create them, and install them with <see cref="TriggerScript"/>.
    /// </summary>
    public bool CreateTriggers { get; set; } = true;

    /// <summary>Wait before the first reconnection after the listening connection was lost; it doubles up to 30 s.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Watches the table of <typeparamref name="TEntity"/>: its <c>[Table]</c> name, otherwise the type name (the name the
    /// change feeds use).
    /// </summary>
    /// <param name="key">The key property; its column value is sent with each change.</param>
    public PostgresNotifications Watch<TEntity>(Expression<Func<TEntity, object?>> key) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(key);
        var body = key.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : key.Body;
        if (body is not MemberExpression { Member: PropertyInfo property })
            throw new ArgumentException($"{key} must select the key property, for example x => x.Id.", nameof(key));

        var table = typeof(TEntity).GetCustomAttribute<TableAttribute>() is { } attribute
            ? new WatchedTable(attribute.Name, attribute.Schema, ColumnName(property))
            : new WatchedTable(typeof(TEntity).Name, null, ColumnName(property));
        return Watch(table);
    }

    /// <summary>Watches a table by name.</summary>
    public PostgresNotifications Watch(WatchedTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _tables.RemoveAll(t => t.Name == table.Name && t.Schema == table.Schema);
        _tables.Add(table);
        return this;
    }

    /// <summary>
    /// The SQL that installs the trigger function and one trigger per watched table: run it yourself when the application
    /// may not create triggers (<see cref="CreateTriggers"/> = false).
    /// </summary>
    public string TriggerScript()
    {
        var sql = new StringBuilder();
        sql.Append($"""
            CREATE OR REPLACE FUNCTION {FunctionName}() RETURNS trigger AS $queryeables$
            DECLARE
                changed jsonb;
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    changed := to_jsonb(OLD);
                ELSE
                    changed := to_jsonb(NEW);
                END IF;
                PERFORM pg_notify('{Channel}', json_build_object(
                    'table', TG_TABLE_NAME,
                    'op', TG_OP,
                    'key', changed ->> TG_ARGV[0])::text);
                RETURN NULL;
            END;
            $queryeables$ LANGUAGE plpgsql;

            """);

        foreach (var table in _tables)
        {
            var trigger = Quote($"{FunctionName}_{table.Name}");
            sql.Append($"DROP TRIGGER IF EXISTS {trigger} ON {table.QualifiedName};\n");
            sql.Append($"CREATE TRIGGER {trigger} AFTER INSERT OR UPDATE OR DELETE ON {table.QualifiedName} ");
            sql.Append($"FOR EACH ROW EXECUTE FUNCTION {FunctionName}({Literal(table.KeyColumn)});\n");
        }
        return sql.ToString();
    }

    private static string ColumnName(PropertyInfo property)
        => property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name;

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

/// <summary>A watched table.</summary>
/// <param name="Name">Table name, as Dommel writes it (quoted, so case-sensitive).</param>
/// <param name="Schema">Schema, or null for the search path.</param>
/// <param name="KeyColumn">Column whose value identifies the changed row.</param>
public sealed record WatchedTable(string Name, string? Schema, string KeyColumn)
{
    internal string QualifiedName => Schema is null
        ? PostgresNotifications.Quote(Name)
        : $"{PostgresNotifications.Quote(Schema)}.{PostgresNotifications.Quote(Name)}";
}

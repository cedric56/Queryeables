using Dommel;
using Microsoft.Extensions.DependencyInjection;
using Queryeables.Abstraction;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;
using System.Reflection;
using System.Text;

namespace Queryeables.Dommel;

/// <summary>
/// Creates the tables of entity types from their classes: the columns Dommel maps, the key (an integer key generated
/// by the database becomes an identity), nullability ([Required], value types), lengths ([StringLength], [MaxLength]).
/// For SQLite, PostgreSQL, SQL Server and MySQL. A table that exists is left as it is: this is no migration tool. Use it
/// for a first application, a prototype, the tests; manage the schema of a database in production with migrations.
/// </summary>
/// <remarks>
/// No foreign key constraints and no indexes besides the key: add them in your migrations. On PostgreSQL a
/// <see cref="DateTime"/> is a <c>timestamp with time zone</c> (the dates Queryeables writes are UTC): Npgsql refuses a
/// <see cref="DateTime"/> whose <see cref="DateTime.Kind"/> is not <see cref="DateTimeKind.Utc"/>. Write UTC dates, use
/// <see cref="DateTimeOffset"/>, or declare <c>[Column(TypeName = "timestamp")]</c> for a local date and time.
/// </remarks>
public static class DommelSchema
{
    /// <summary>The script that creates the tables of <paramref name="types"/> (when missing) on <paramref name="sqlBuilder"/>'s database.</summary>
    public static string CreateScript(IEnumerable<Type> types, ISqlBuilder sqlBuilder)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(sqlBuilder);
        var sql = new StringBuilder();
        foreach (var type in types)
            sql.Append(CreateTable(type, sqlBuilder)).Append('\n');
        return sql.ToString();
    }

    /// <summary>Creates the tables of <paramref name="types"/> that do not exist yet.</summary>
    public static async Task CreateTablesAsync(DbProviderFactory factory, IEnumerable<Type> types, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(types);
        await using var connection = factory.CreateConnection()
            ?? throw new InvalidOperationException("The factory could not create a connection.");
        await connection.OpenAsync(ct);
        var sqlBuilder = DommelMapper.GetSqlBuilder(connection);
        foreach (var type in types)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = CreateTable(type, sqlBuilder);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private enum Dialect
    {
        Sqlite,
        Postgres,
        SqlServer,
        MySql,
    }

    private static Dialect DialectOf(ISqlBuilder sqlBuilder) => sqlBuilder switch
    {
        SqliteSqlBuilder => Dialect.Sqlite,
        PostgresSqlBuilder => Dialect.Postgres,
        SqlServerSqlBuilder => Dialect.SqlServer,
        MySqlSqlBuilder => Dialect.MySql,
        _ => throw new NotSupportedException(
            $"Creating tables is not supported for {sqlBuilder.GetType().Name}: create them with your migrations."),
    };

    private static string CreateTable(Type type, ISqlBuilder sqlBuilder)
    {
        var dialect = DialectOf(sqlBuilder);
        var table = Resolvers.Table(type, sqlBuilder);
        var keys = Resolvers.KeyProperties(type);
        var identity = keys.Length == 1 && keys[0].IsGenerated && IsInteger(keys[0].Property.PropertyType) ? keys[0].Property : null;
        var nullability = new NullabilityInfoContext();

        var columns = new List<string>();
        foreach (var column in Resolvers.Properties(type))
        {
            var property = column.Property;
            var name = Resolvers.Column(property, sqlBuilder, false);
            var isKey = keys.Any(k => k.Property == property);
            if (property == identity)
            {
                columns.Add($"{name} {IdentityColumn(dialect, property.PropertyType)}");
                continue;
            }
            var nullable = !isKey && IsNullable(property, nullability);
            columns.Add($"{name} {ColumnType(dialect, property, isKey)}{(nullable ? "" : " NOT NULL")}");
        }
        if (identity is null)
            columns.Add($"PRIMARY KEY ({string.Join(", ", keys.Select(k => Resolvers.Column(k.Property, sqlBuilder, false)))})");

        var body = $"{table} (\n    {string.Join(",\n    ", columns)}\n)";
        return dialect == Dialect.SqlServer
            ? $"IF OBJECT_ID(N'{table.Replace("'", "''", StringComparison.Ordinal)}', N'U') IS NULL CREATE TABLE {body};"
            : $"CREATE TABLE IF NOT EXISTS {body};";
    }

    private static string IdentityColumn(Dialect dialect, Type type) => dialect switch
    {
        Dialect.Sqlite => "INTEGER PRIMARY KEY AUTOINCREMENT",      // the rowid: what the change hooks report
        Dialect.Postgres => $"{(Core(type) == typeof(long) ? "bigint" : "integer")} GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY",
        Dialect.SqlServer => $"{(Core(type) == typeof(long) ? "bigint" : "int")} IDENTITY(1,1) PRIMARY KEY",
        _ => $"{(Core(type) == typeof(long) ? "BIGINT" : "INT")} AUTO_INCREMENT PRIMARY KEY",
    };

    private static string ColumnType(Dialect dialect, PropertyInfo property, bool isKey)
    {
        var type = Core(property.PropertyType);
        var length = property.GetCustomAttribute<StringLengthAttribute>()?.MaximumLength
                     ?? property.GetCustomAttribute<MaxLengthAttribute>()?.Length;
        if (property.GetCustomAttribute<ColumnAttribute>()?.TypeName is { Length: > 0 } declared)
            return declared;
        if (type.IsEnum)
            type = Enum.GetUnderlyingType(type);

        return dialect switch
        {
            Dialect.Sqlite => type == typeof(bool) || IsInteger(type) ? "INTEGER"
                : type == typeof(double) || type == typeof(float) ? "REAL"
                : type == typeof(byte[]) ? "BLOB"
                : "TEXT",                                           // text, decimal (exact), dates, Guid
            Dialect.Postgres => Name(type) switch
            {
                nameof(Boolean) => "boolean",
                nameof(Byte) or nameof(SByte) or nameof(Int16) => "smallint",
                nameof(Int32) or nameof(UInt16) => "integer",
                nameof(Int64) or nameof(UInt32) or nameof(UInt64) => "bigint",
                nameof(Single) => "real",
                nameof(Double) => "double precision",
                nameof(Decimal) => "numeric",
                nameof(DateTime) or nameof(DateTimeOffset) => "timestamp with time zone",
                nameof(DateOnly) => "date",
                nameof(TimeOnly) => "time",
                nameof(TimeSpan) => "interval",
                nameof(Guid) => "uuid",
                _ when type == typeof(byte[]) => "bytea",
                _ => length is { } n ? $"varchar({n})" : "text",
            },
            Dialect.SqlServer => Name(type) switch
            {
                nameof(Boolean) => "bit",
                nameof(Byte) => "tinyint",
                nameof(SByte) or nameof(Int16) => "smallint",
                nameof(Int32) or nameof(UInt16) => "int",
                nameof(Int64) or nameof(UInt32) or nameof(UInt64) => "bigint",
                nameof(Single) => "real",
                nameof(Double) => "float",
                nameof(Decimal) => "decimal(19, 4)",
                nameof(DateTime) => "datetime2",
                nameof(DateTimeOffset) => "datetimeoffset",
                nameof(DateOnly) => "date",
                nameof(TimeOnly) or nameof(TimeSpan) => "time",
                nameof(Guid) => "uniqueidentifier",
                _ when type == typeof(byte[]) => length is { } b ? $"varbinary({b})" : isKey ? "varbinary(900)" : "varbinary(max)",
                _ => length is { } n ? $"nvarchar({n})" : isKey ? "nvarchar(450)" : "nvarchar(max)",
            },
            _ => Name(type) switch
            {
                nameof(Boolean) => "tinyint(1)",
                nameof(Byte) => "tinyint unsigned",
                nameof(SByte) => "tinyint",
                nameof(Int16) => "smallint",
                nameof(Int32) or nameof(UInt16) => "int",
                nameof(Int64) or nameof(UInt32) => "bigint",
                nameof(UInt64) => "bigint unsigned",
                nameof(Single) => "float",
                nameof(Double) => "double",
                nameof(Decimal) => "decimal(19, 4)",
                nameof(DateTime) or nameof(DateTimeOffset) => "datetime(6)",
                nameof(DateOnly) => "date",
                nameof(TimeOnly) or nameof(TimeSpan) => "time(6)",
                nameof(Guid) => "char(36)",
                _ when type == typeof(byte[]) => length is { } b ? $"varbinary({b})" : isKey ? "varbinary(255)" : "longblob",
                _ => length is { } n ? $"varchar({n})" : isKey ? "varchar(255)" : "longtext",
            },
        };
    }

    private static string Name(Type type) => type == typeof(byte[]) ? "" : type.Name;

    private static Type Core(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsInteger(Type type)
        => Type.GetTypeCode(Core(type)) is TypeCode.Int64 or TypeCode.Int32 or TypeCode.Int16 or TypeCode.Byte or TypeCode.SByte
               or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64
           && !Core(type).IsEnum;

    /// <summary>Null allowed: a nullable value type, or a reference type not marked [Required] nor declared non-nullable.</summary>
    private static bool IsNullable(PropertyInfo property, NullabilityInfoContext nullability)
    {
        if (property.GetCustomAttribute<RequiredAttribute>() is not null)
            return false;
        if (property.PropertyType.IsValueType)
            return Nullable.GetUnderlyingType(property.PropertyType) is not null;
        return nullability.Create(property).WriteState != NullabilityState.NotNull;
    }
}

/// <summary>
/// Table names for Dommel: the [Table] attribute, otherwise the name of the class (<c>Person</c>), not Dommel's plural
/// (<c>Persons</c>) unless asked for. The name the change feeds and the PostgreSQL triggers use too.
/// </summary>
public sealed class QueryeablesTableNameResolver(bool pluralize = false) : DefaultTableNameResolver
{
    public override string ResolveTableName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return pluralize || type.GetCustomAttribute<TableAttribute>() is not null ? base.ResolveTableName(type) : type.Name;
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Queryeables.Abstraction;

/// <summary>
/// Text representation of an entity key, carried by <see cref="Record{T}"/>:
/// works for any key type (long, int, Guid, string, enum, date) and for composite keys as tuples.
/// </summary>
/// <example>
/// <code>
/// RecordKey.Format(42L);                       // "42"
/// RecordKey.Format((OrderId: 7, Line: 2));     // composite key
/// RecordKey.Parse&lt;long&gt;("42");             // 42
/// RecordKey.Parse&lt;(int, int)&gt;(text);       // (7, 2)
/// </code>
/// </example>
public static class RecordKey
{
    // Separator of the parts of a composite key: a control character, absent from ordinary values.
    private const char Separator = '\u001F';

    /// <summary>Converts a key to text, independently of the culture.</summary>
    public static string Format(object key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key is ITuple tuple)
        {
            if (tuple.Length > 7)
                throw new ArgumentException("A composite key is limited to 7 elements.", nameof(key));

            var parts = new string[tuple.Length];
            for (var i = 0; i < tuple.Length; i++)
                parts[i] = FormatPart(tuple[i]);
            return string.Join(Separator, parts);
        }

        return FormatPart(key);
    }

    /// <summary>Reads back a key produced by <see cref="Format"/>.</summary>
    public static TKey Parse<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TKey>(string text)
        where TKey : notnull
        => (TKey)Parse(text, typeof(TKey));

    /// <inheritdoc cref="Parse{TKey}(string)"/>
    public static object Parse(string text, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(type);

        if (!IsValueTuple(type))
            return ParsePart(text, type);

        var elementTypes = type.GetGenericArguments();
        var parts = text.Split(Separator);
        if (parts.Length != elementTypes.Length)
            throw new FormatException($"The key '{text}' does not have {elementTypes.Length} elements.");

        var values = new object[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            values[i] = ParsePart(parts[i], elementTypes[i]);
        return Activator.CreateInstance(type, values)!;
    }

    private static string FormatPart(object? value) => value switch
    {
        null => throw new ArgumentException("A key cannot contain null."),
        string s when s.Contains(Separator) => throw new ArgumentException("The key contains a reserved character (U+001F)."),
        string s => s,
        Guid g => g.ToString("D"),
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? throw new ArgumentException($"Key of type {value.GetType().Name} cannot be converted to text.")
    };

    /// <summary>
    /// Explicit list of supported key types rather than TypeDescriptor converters: compatible with trimming and Native AOT.
    /// </summary>
    private static object ParsePart(string text, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        var invariant = CultureInfo.InvariantCulture;

        if (target.IsEnum)
            return Enum.Parse(target, text);

        return Type.GetTypeCode(target) switch
        {
            TypeCode.String => text,
            TypeCode.Int64 => long.Parse(text, invariant),
            TypeCode.Int32 => int.Parse(text, invariant),
            TypeCode.Int16 => short.Parse(text, invariant),
            TypeCode.SByte => sbyte.Parse(text, invariant),
            TypeCode.UInt64 => ulong.Parse(text, invariant),
            TypeCode.UInt32 => uint.Parse(text, invariant),
            TypeCode.UInt16 => ushort.Parse(text, invariant),
            TypeCode.Byte => byte.Parse(text, invariant),
            TypeCode.Decimal => decimal.Parse(text, invariant),
            TypeCode.Double => double.Parse(text, invariant),
            TypeCode.Single => float.Parse(text, invariant),
            TypeCode.Boolean => bool.Parse(text),
            TypeCode.Char => char.Parse(text),
            TypeCode.DateTime => DateTime.Parse(text, invariant, DateTimeStyles.RoundtripKind),
            _ when target == typeof(Guid) => Guid.Parse(text),
            _ when target == typeof(DateTimeOffset) => DateTimeOffset.Parse(text, invariant, DateTimeStyles.RoundtripKind),
            _ when target == typeof(TimeSpan) => TimeSpan.Parse(text, invariant),
            _ when target == typeof(DateOnly) => DateOnly.Parse(text, invariant),
            _ => throw new NotSupportedException($"Unsupported key type: {type.Name}.")
        };
    }

    private static bool IsValueTuple(Type type)
        => type.IsGenericType && type.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true;
}

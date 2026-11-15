using System.Globalization;

namespace Queryeables.Remote;

/// <summary>The values of filter conditions as a transport delivers them, made plain and converted to their property's type.</summary>
public static class FilterValues
{
    /// <summary>A value as a transport delivers it, made plain: a JSON element becomes its text, number, bool, null or list.</summary>
    public static object? Plain(object? value) => value is System.Text.Json.JsonElement json ? Plain(json) : value;

    private static object? Plain(System.Text.Json.JsonElement json) => json.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => json.GetString(),
        System.Text.Json.JsonValueKind.Number => json.TryGetInt64(out var integer) ? integer
            : json.TryGetDecimal(out var number) ? number
            : json.TryGetDouble(out var real) ? real
            : throw new ArgumentException($"Invalid number: {json.GetRawText()}."),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Array => json.EnumerateArray().Select(Plain).ToArray(),
        System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => null,
        _ => json.GetRawText(),
    };

    /// <summary>
    /// Converts a received value to the property type. A serializer may not keep the exact type
    /// of a number stored in an object (a 5 comes back as a byte): without conversion, Expression.Constant fails.
    /// </summary>
    public static object? ConvertTo(object? value, Type type, string name)
    {
        value = Plain(value);
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (value is null)
        {
            if (type.IsValueType && target == type)
                throw new ArgumentException($"Property '{name}' ({type.Name}) does not accept null.");
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

            if (target == typeof(DateOnly))
                return value is DateTime day ? DateOnly.FromDateTime(day) : DateOnly.Parse(Convert.ToString(value, invariant)!, invariant);

            if (target == typeof(TimeOnly))
                return value is TimeSpan time ? TimeOnly.FromTimeSpan(time) : TimeOnly.Parse(Convert.ToString(value, invariant)!, invariant);

            if (target == typeof(object))
                return value;

            return Convert.ChangeType(value, target, invariant);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new ArgumentException(
                $"Invalid value '{value}' ({value.GetType().Name}) for property '{name}' ({type.Name}).", ex);
        }
    }
}

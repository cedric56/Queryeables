using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Queryeables.Abstraction;

/// <summary>
/// Représentation texte d'une clé d'entité, transportée par <see cref="Record{T}"/> :
/// fonctionne pour tout type de clé (long, int, Guid, string, enum, date) et pour les clés composites en tuple.
/// </summary>
/// <example>
/// <code>
/// RecordKey.Format(42L);                    // "42"
/// RecordKey.Format((CommandeId: 7, Ligne: 2)); // clé composite
/// RecordKey.Parse&lt;long&gt;("42");             // 42
/// RecordKey.Parse&lt;(int, int)&gt;(texte);      // (7, 2)
/// </code>
/// </example>
public static class RecordKey
{
    // Séparateur des parties d'une clé composite : caractère de contrôle, absent des valeurs ordinaires.
    private const char Separator = '\u001F';

    /// <summary>Convertit une clé en texte, indépendamment de la culture.</summary>
    public static string Format(object key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key is ITuple tuple)
        {
            if (tuple.Length > 7)
                throw new ArgumentException("Une clé composite est limitée à 7 éléments.", nameof(key));

            var parts = new string[tuple.Length];
            for (var i = 0; i < tuple.Length; i++)
                parts[i] = FormatPart(tuple[i]);
            return string.Join(Separator, parts);
        }

        return FormatPart(key);
    }

    /// <summary>Relit une clé produite par <see cref="Format"/>.</summary>
    public static TKey Parse<TKey>(string text) where TKey : notnull
        => (TKey)Parse(text, typeof(TKey));

    /// <inheritdoc cref="Parse{TKey}(string)"/>
    public static object Parse(string text, Type type)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(type);

        if (!IsValueTuple(type))
            return ParsePart(text, type);

        var elementTypes = type.GetGenericArguments();
        var parts = text.Split(Separator);
        if (parts.Length != elementTypes.Length)
            throw new FormatException($"La clé « {text} » n'a pas {elementTypes.Length} éléments.");

        var values = new object[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            values[i] = ParsePart(parts[i], elementTypes[i]);
        return Activator.CreateInstance(type, values)!;
    }

    private static string FormatPart(object? value) => value switch
    {
        null => throw new ArgumentException("Une clé ne peut pas contenir null."),
        string s when s.Contains(Separator) => throw new ArgumentException("La clé contient un caractère réservé (U+001F)."),
        string s => s,
        Guid g => g.ToString("D"),
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? throw new ArgumentException($"Clé de type {value.GetType().Name} non convertible en texte.")
    };

    private static object ParsePart(string text, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        var invariant = CultureInfo.InvariantCulture;

        if (target == typeof(string))
            return text;
        if (target == typeof(DateTime))
            return DateTime.Parse(text, invariant, DateTimeStyles.RoundtripKind);
        if (target == typeof(DateTimeOffset))
            return DateTimeOffset.Parse(text, invariant, DateTimeStyles.RoundtripKind);

        // Entiers, Guid, enums, décimaux... : convertisseur standard du type.
        return TypeDescriptor.GetConverter(target).ConvertFromInvariantString(text)
            ?? throw new FormatException($"Clé « {text} » invalide pour le type {type.Name}.");
    }

    private static bool IsValueTuple(Type type)
        => type.IsGenericType && type.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true;
}

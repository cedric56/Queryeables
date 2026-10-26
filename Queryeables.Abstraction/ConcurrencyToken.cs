using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Queryeables.Abstraction;

/// <summary>
/// Concurrence optimiste : une propriété entière marquée [ConcurrencyCheck] sert de numéro de version.
/// Les stores vérifient qu'elle n'a pas changé en base avant une mise à jour ou une suppression,
/// puis l'incrémentent. Sinon, <see cref="EntityStoreException"/> avec <see cref="EntityErrorCode.Conflict"/>.
/// </summary>
/// <example>
/// <code>
/// public class Client
/// {
///     public long Id { get; set; }
///     [ConcurrencyCheck] public long Version { get; set; }
/// }
/// </code>
/// </example>
public static class ConcurrencyToken
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> _properties = new();

    /// <summary>Propriété de version du type, ou null s'il n'en a pas.</summary>
    public static PropertyInfo? Find(Type type) => _properties.GetOrAdd(type, static t =>
    {
        var property = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(p => p.GetCustomAttribute<ConcurrencyCheckAttribute>() is not null);

        if (property is not null && property.PropertyType != typeof(long) && property.PropertyType != typeof(int))
            throw new NotSupportedException(
                $"{t.Name}.{property.Name} : [ConcurrencyCheck] n'est pris en charge que sur un int ou un long.");

        return property;
    });

    public static long Get(object entity, PropertyInfo property)
        => Convert.ToInt64(property.GetValue(entity));

    public static void Set(object entity, PropertyInfo property, long value)
        => property.SetValue(entity, Box(property, value));

    /// <summary>Valeur typée comme la propriété (int ou long), pour les API qui l'exigent.</summary>
    /// <remarks>Les deux branches sont converties en object : avec « ? : », un int serait promu en long.</remarks>
    public static object Box(PropertyInfo property, long value)
        => property.PropertyType == typeof(int) ? (object)checked((int)value) : (object)value;

    public static EntityStoreException ConflictFor(Type type)
        => new(EntityErrorCode.Conflict,
            $"{type.Name} a été modifié par quelqu'un d'autre depuis sa lecture. Rechargez-le avant de réessayer.");
}

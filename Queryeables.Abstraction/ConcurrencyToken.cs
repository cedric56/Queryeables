using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Queryeables.Abstraction;

/// <summary>
/// Optimistic concurrency: an integer property marked [ConcurrencyCheck] acts as a version number.
/// Stores check that it did not change in the database before an update or a delete, then increment it.
/// Otherwise they throw <see cref="EntityStoreException"/> with <see cref="EntityErrorCode.Conflict"/>.
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

    /// <summary>Version property of the type, or null if it has none.</summary>
    public static PropertyInfo? Find([DynamicallyAccessedMembers(EntityMembers.Required)] Type type)
    {
        if (_properties.TryGetValue(type, out var cached))
            return cached;

        var property = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(p => p.GetCustomAttribute<ConcurrencyCheckAttribute>() is not null);

        if (property is not null && property.PropertyType != typeof(long) && property.PropertyType != typeof(int))
            throw new NotSupportedException(
                $"{type.Name}.{property.Name}: [ConcurrencyCheck] is only supported on an int or a long.");

        _properties.TryAdd(type, property);
        return property;
    }

    public static long Get(object entity, PropertyInfo property)
        => Convert.ToInt64(property.GetValue(entity));

    public static void Set(object entity, PropertyInfo property, long value)
        => property.SetValue(entity, Box(property, value));

    /// <summary>Value typed like the property (int or long), for APIs that require it.</summary>
    /// <remarks>Both branches are cast to object: with "? :", an int would be promoted to long.</remarks>
    public static object Box(PropertyInfo property, long value)
        => property.PropertyType == typeof(int) ? (object)checked((int)value) : (object)value;

    public static EntityStoreException ConflictFor(Type type)
        => new(EntityErrorCode.Conflict,
            $"{type.Name} was modified by someone else since it was read. Reload it before trying again.");
}

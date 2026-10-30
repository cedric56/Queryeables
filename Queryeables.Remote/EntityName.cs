using Queryeables.Abstraction;
using System.Collections.Concurrent;
using System.Reflection;

namespace Queryeables.Remote;

/// <summary>
/// Network name of an entity type: <see cref="RemoteNameAttribute"/> when present, otherwise the full name.
/// Client and server use the same rule, which avoids collisions between two classes with the same name.
/// </summary>
public static class EntityName
{
    private static readonly ConcurrentDictionary<Type, string> _names = new();

    public static string Of<TEntity>() => Of(typeof(TEntity));

    public static string Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _names.GetOrAdd(type, static t =>
            t.GetCustomAttribute<RemoteNameAttribute>()?.Name
            ?? t.FullName
            ?? throw new ArgumentException($"Type {t} has no full name.", nameof(type)));
    }
}

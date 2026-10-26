using Queryeables.Abstraction;
using System.Collections.Concurrent;
using System.Reflection;

namespace Queryeables.Onion;

/// <summary>
/// Nom d'un type d'entité sur le réseau : <see cref="RemoteNameAttribute"/> s'il est présent, sinon le nom complet.
/// Client et serveur utilisent la même règle, ce qui évite les collisions entre deux classes de même nom.
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
            ?? throw new ArgumentException($"Le type {t} n'a pas de nom complet.", nameof(type)));
    }
}

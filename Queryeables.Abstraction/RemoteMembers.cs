using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Queryeables.Abstraction;

/// <summary>
/// The property cannot be used by remote clients to filter or sort. A property the serializer leaves out
/// (<c>[IgnoreMember]</c>, <c>[JsonIgnore]</c>, <c>[IgnoreDataMember]</c>) is already excluded: what a client cannot read,
/// it must not be able to guess either, one comparison at a time ("starts with a", "starts with ab"...).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotQueryableAttribute : Attribute;

/// <summary>
/// The server decides the value, never a remote client: on an update, the stored value is kept whatever the client sent;
/// on an insert, the value the client sent is replaced by the default of its type (null, 0, false), and the database or
/// the server's own code sets it. For a creator, an approval flag, a role, a balance...
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ServerOwnedAttribute : Attribute;

/// <summary>What remote clients may do with the properties of <typeparamref name="T"/> (see the attributes above).</summary>
public static class RemoteMembers<[DynamicallyAccessedMembers(EntityMembers.Required)] T> where T : class
{
    private static readonly string[] Hidden = ["IgnoreMemberAttribute", "JsonIgnoreAttribute", "IgnoreDataMemberAttribute", nameof(NotQueryableAttribute)];

    /// <summary>The properties a client may filter and sort on: public, readable, neither hidden from the serializer nor [NotQueryable].</summary>
    public static IReadOnlySet<string> Queryable { get; } = typeof(T)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0
                    && !p.GetCustomAttributes(inherit: true).Any(a => Hidden.Contains(a.GetType().Name)))
        .Select(p => p.Name)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>The [ServerOwned] properties: their value never comes from a client.</summary>
    public static IReadOnlyList<PropertyInfo> ServerOwned { get; } = typeof(T)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.IsDefined(typeof(ServerOwnedAttribute), inherit: true))
        .ToArray();

    /// <summary>Puts the stored values of the [ServerOwned] properties into <paramref name="received"/>.</summary>
    public static void KeepServerValues(T received, T stored)
    {
        foreach (var property in ServerOwned)
            property.SetValue(received, property.GetValue(stored));
    }

    /// <summary>Sets the [ServerOwned] properties of <paramref name="received"/> to the default of their type.</summary>
    public static void DiscardClientValues(T received)
    {
        foreach (var property in ServerOwned)
            property.SetValue(received, null);      // a value type gets its default
    }
}

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

/// <summary>The attributes by which serializers leave a property out.</summary>
public static class RemoteMembers
{
    /// <summary>
    /// The attributes that leave a property out of some serializer: <c>[IgnoreMember]</c> (MessagePack),
    /// <c>[JsonIgnore]</c> (System.Text.Json, Newtonsoft), <c>[IgnoreDataMember]</c> (MessagePack, DataContract). Which
    /// one a given serializer honors is its own business (<c>IEntitySerializer.Omits</c>).
    /// </summary>
    public static IReadOnlyList<string> HidingAttributes { get; } = ["IgnoreMemberAttribute", "JsonIgnoreAttribute", "IgnoreDataMemberAttribute"];

    /// <summary>Whether the property carries one of <see cref="HidingAttributes"/>.</summary>
    public static bool IsMarkedHidden(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        // [JsonIgnore(Condition = WhenWritingNull)] still sends the value: only "always" leaves it out.
        return property.GetCustomAttributes(inherit: true).Any(a => a is System.Text.Json.Serialization.JsonIgnoreAttribute json
            ? json.Condition == System.Text.Json.Serialization.JsonIgnoreCondition.Always
            : HidingAttributes.Contains(a.GetType().Name));
    }

    /// <summary>Whether the property carries an attribute of this name (<c>IgnoreMemberAttribute</c>...).</summary>
    public static bool Has(PropertyInfo property, string attribute)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.GetCustomAttributes(inherit: true).Any(a => a.GetType().Name == attribute);
    }
}

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
    public static void KeepServerValues(T received, T stored) => KeepValues(received, stored, ServerOwned);

    /// <summary>Puts the stored values of <paramref name="properties"/> into <paramref name="received"/>.</summary>
    public static void KeepValues(T received, T stored, IEnumerable<PropertyInfo> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        foreach (var property in properties)
            property.SetValue(received, property.GetValue(stored));
    }

    /// <summary>
    /// The writable properties marked to be left out of serialization (<c>[IgnoreMember]</c>, <c>[JsonIgnore]</c>,
    /// <c>[IgnoreDataMember]</c>): a client never sends them, so an update keeps their stored values (a password hash...).
    /// </summary>
    public static IReadOnlyList<PropertyInfo> MarkedHidden { get; } = typeof(T)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && RemoteMembers.IsMarkedHidden(p))
        .ToArray();

    /// <summary>Sets the [ServerOwned] properties of <paramref name="received"/> to the default of their type.</summary>
    public static void DiscardClientValues(T received)
    {
        foreach (var property in ServerOwned)
            property.SetValue(received, null);      // a value type gets its default
    }

    /// <summary>
    /// The numeric key a remote client sent with an insert, back to 0: the database numbers the row, a client never picks
    /// it (it could take a number the sequence will give next, or one a deleted row had). A key the application assigns
    /// itself is kept: <c>[DatabaseGenerated(DatabaseGeneratedOption.None)]</c>, or a key that is not an integer (a Guid).
    /// </summary>
    public static void DiscardClientKey(T received)
    {
        if (GeneratedKey is { } key)
            key.SetValue(received, null);
    }

    private static readonly PropertyInfo? GeneratedKey = SyncMetadata.Key(typeof(T)) is { CanWrite: true } key
        && Type.GetTypeCode(key.PropertyType) is TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64
        && key.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedAttribute>(inherit: true)
            is not { DatabaseGeneratedOption: System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.None }
        ? key
        : null;
}

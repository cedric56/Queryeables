using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Queryeables.Abstraction;

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

/// <summary>The properties of <typeparamref name="T"/> a serializer may leave out, as remote clients see them.</summary>
public static class RemoteMembers<[DynamicallyAccessedMembers(EntityMembers.Required)] T> where T : class
{
    /// <summary>
    /// The writable properties marked to be left out of serialization (<c>[IgnoreMember]</c>, <c>[JsonIgnore]</c>,
    /// <c>[IgnoreDataMember]</c>): the server checks that its serializer honors their attribute, so that their values
    /// (a password hash...) never reach a client.
    /// </summary>
    public static IReadOnlyList<PropertyInfo> MarkedHidden { get; } = typeof(T)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && RemoteMembers.IsMarkedHidden(p))
        .ToArray();
}

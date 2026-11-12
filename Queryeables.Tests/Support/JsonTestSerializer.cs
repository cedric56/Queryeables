using Queryeables.Remote;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Queryeables.Tests;

/// <summary>A serializer that is not MessagePack: the remote store must work with any transport and any serializer.</summary>
public sealed class JsonTestSerializer : IEntitySerializer
{
    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    public T? Deserialize<T>(byte[] data) => JsonSerializer.Deserialize<T>(data);

    /// <summary>System.Text.Json leaves out <c>[JsonIgnore]</c> (always), and nothing else.</summary>
    public bool Omits(PropertyInfo property)
        => property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always };
}

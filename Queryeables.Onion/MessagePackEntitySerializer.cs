using MessagePack;
using Queryeables.Remote;

namespace Queryeables.Onion;

/// <summary>
/// <see cref="IEntitySerializer"/> with MessagePack, the serializer of MagicOnion. Entities need <c>[MessagePackObject]</c>
/// (or contractless options) and, under Native AOT, source-generated formatters.
/// </summary>
public sealed class MessagePackEntitySerializer(MessagePackSerializerOptions? options = null) : IEntitySerializer
{
    /// <summary>With the default MessagePack options.</summary>
    public static MessagePackEntitySerializer Default { get; } = new();

    public byte[] Serialize<T>(T value) => MessagePackSerializer.Serialize(value, options);

    public T? Deserialize<T>(byte[] data) => MessagePackSerializer.Deserialize<T>(data, options);
}

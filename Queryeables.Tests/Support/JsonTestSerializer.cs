using Queryeables.Remote;
using System.Text.Json;

namespace Queryeables.Tests;

/// <summary>A serializer that is not MessagePack: the remote store must work with any transport and any serializer.</summary>
public sealed class JsonTestSerializer : IEntitySerializer
{
    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    public T? Deserialize<T>(byte[] data) => JsonSerializer.Deserialize<T>(data);
}

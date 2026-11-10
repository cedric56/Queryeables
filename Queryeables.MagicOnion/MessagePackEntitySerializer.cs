using MessagePack;
using Queryeables.Remote;

namespace Queryeables.MagicOnion;

/// <summary>
/// <see cref="IEntitySerializer"/> with MessagePack, the serializer of MagicOnion. Entities need <c>[MessagePackObject]</c>
/// (or contractless options) and, under Native AOT, source-generated formatters.
/// </summary>
/// <param name="options">The options; <see cref="MessagePackSerializer.DefaultOptions"/> when null, read at each call (an
/// application may set its resolver at startup).</param>
/// <param name="untrustedData">
/// Reads with <see cref="MessagePackSecurity.UntrustedData"/>: what a server receives comes from clients it does not
/// control (hash collisions, deep nesting).
/// </param>
public sealed class MessagePackEntitySerializer(MessagePackSerializerOptions? options = null, bool untrustedData = false) : IEntitySerializer
{
    private (MessagePackSerializerOptions From, MessagePackSerializerOptions Hardened)? _reading;

    /// <summary>With the default MessagePack options.</summary>
    public static MessagePackEntitySerializer Default { get; } = new();

    /// <summary>With the default MessagePack options, reading as untrusted data: the one a server uses.</summary>
    public static MessagePackEntitySerializer Server { get; } = new(untrustedData: true);

    public byte[] Serialize<T>(T value) => MessagePackSerializer.Serialize(value, options);

    public T? Deserialize<T>(byte[] data) => MessagePackSerializer.Deserialize<T>(data, ReadingOptions());

    private MessagePackSerializerOptions? ReadingOptions()
    {
        if (!untrustedData)
            return options;

        var from = options ?? MessagePackSerializer.DefaultOptions;
        if (_reading is { } cached && ReferenceEquals(cached.From, from))
            return cached.Hardened;
        var hardened = from.WithSecurity(MessagePackSecurity.UntrustedData);
        _reading = (from, hardened);
        return hardened;
    }
}

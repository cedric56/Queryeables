namespace Queryeables.Remote;

/// <summary>
/// Turns entities (and the few values the protocol carries: arrays of entities, booleans, arrays of byte arrays) into
/// bytes and back. The transport provides it: MessagePack for MagicOnion, for example. Client and server must use the same.
/// </summary>
public interface IEntitySerializer
{
    byte[] Serialize<T>(T value);

    T? Deserialize<T>(byte[] data);
}

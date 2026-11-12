using System.Reflection;
using Queryeables.Abstraction;

namespace Queryeables.Remote;

/// <summary>
/// Turns entities (and the few values the protocol carries: arrays of entities, booleans, arrays of byte arrays) into
/// bytes and back. The transport provides it: MessagePack for MagicOnion, for example. Client and server must use the same.
/// </summary>
public interface IEntitySerializer
{
    byte[] Serialize<T>(T value);

    T? Deserialize<T>(byte[] data);

    /// <summary>
    /// Whether this serializer leaves the property out: a client neither reads nor sends it, an update keeps its stored
    /// value. MessagePack honors <c>[IgnoreMember]</c> and <c>[IgnoreDataMember]</c>, System.Text.Json <c>[JsonIgnore]</c>.
    /// The default: any of them (<see cref="RemoteMembers.IsMarkedHidden"/>). A server refuses an entity marked with an
    /// attribute its serializer does not honor: the value would reach every client.
    /// </summary>
    bool Omits(PropertyInfo property) => RemoteMembers.IsMarkedHidden(property);
}

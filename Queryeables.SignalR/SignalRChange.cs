using MessagePack;
using Queryeables.Abstraction;

namespace Queryeables.SignalR;

/// <summary>
/// Wire form of <see cref="EntityChange{T}"/> for the SignalR hub (MessagePack protocol). Annotated so that the MessagePack
/// source generator can produce its formatter at build time (no runtime code generation, AOT friendly).
/// </summary>
[MessagePackObject]
public sealed class SignalRChange
{
    [Key(0)]
    public string EntityKey { get; set; } = string.Empty;

    [Key(1)]
    public ChangeType ChangeType { get; set; }

    [Key(2)]
    public List<string>? Connections { get; set; }

    public static SignalRChange From<T>(EntityChange<T> record) where T : class
    {
        ArgumentNullException.ThrowIfNull(record);
        return new() { EntityKey = record.EntityKey, ChangeType = record.ChangeType, Connections = record.Connections };
    }

    public EntityChange<T> ToChange<T>() where T : class => new(EntityKey, ChangeType, Connections);
}

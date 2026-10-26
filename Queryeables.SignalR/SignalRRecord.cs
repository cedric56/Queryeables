using MessagePack;
using Queryeables.Abstraction;

namespace Queryeables.SignalR;

/// <summary>
/// Wire form of <see cref="Record{T}"/> for the SignalR hub (MessagePack protocol). Annotated so that the MessagePack
/// source generator can produce its formatter at build time (no runtime code generation, AOT friendly).
/// </summary>
[MessagePackObject]
public sealed class SignalRRecord
{
    [Key(0)]
    public string EntityKey { get; set; } = string.Empty;

    [Key(1)]
    public ChangeType ChangeType { get; set; }

    [Key(2)]
    public List<string>? Connections { get; set; }

    public static SignalRRecord From<T>(Record<T> record) where T : class
    {
        ArgumentNullException.ThrowIfNull(record);
        return new() { EntityKey = record.EntityKey, ChangeType = record.ChangeType, Connections = record.Connections };
    }

    public Record<T> ToRecord<T>() where T : class => new(EntityKey, ChangeType, Connections);
}

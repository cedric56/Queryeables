using MessagePack;
using Queryeables.Abstraction;

namespace Queryeables.Onion;

/// <summary>
/// Wire form of <see cref="DbRecord{T}"/> for the MagicOnion hub. Annotated for MessagePack so that its source
/// generator can produce the formatter at build time (no runtime code generation, AOT friendly).
/// </summary>
[MessagePackObject]
public sealed class RecordMessage
{
    [Key(0)]
    public string EntityKey { get; set; } = string.Empty;

    [Key(1)]
    public ChangeType ChangeType { get; set; }

    [Key(2)]
    public List<string>? Connections { get; set; }

    public static RecordMessage From<T>(DbRecord<T> record) where T : class
    {
        ArgumentNullException.ThrowIfNull(record);
        return new() { EntityKey = record.EntityKey, ChangeType = record.ChangeType, Connections = record.Connections };
    }

    public DbRecord<T> ToRecord<T>() where T : class => new(EntityKey, ChangeType, Connections);
}

using MessagePack;
using KeyAttribute = MessagePack.KeyAttribute;

namespace Queryeables.Abstraction;

[MessagePackObject]
public class Record<T> where T : class, new()
{
    public Record()
    {
        ChangeType = ChangeType.None;
    }

    public Record(long rowId, ChangeType changeType, List<string> connections)
    {
        RowId = rowId;
        ChangeType = changeType;
        Connections = connections;
    }

    [Key(0)]
    public long RowId { get; set; }

    [Key(1)]
    public ChangeType ChangeType { get; set; }

    [Key(2)]
    public List<string>? Connections { get; set; }

    public override string ToString()
    {
        return $"{typeof(T).Name} : {RowId} {ChangeType} {string.Join(",", Connections ?? [])}";
    }

    public void Add(string? connectionId)
    {
        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            Connections ??= [];
            Connections.Add(connectionId);
        }
    }

    public bool Contains(string? connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return false;

        return (Connections ?? []).Contains(connectionId);
    }
}

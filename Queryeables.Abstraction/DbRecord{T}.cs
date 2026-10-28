namespace Queryeables.Abstraction;

/// <summary>
/// A row change: which entity, what kind of change, and which clients already know about it.
/// Serializer-agnostic: each transport (MagicOnion, SignalR...) maps it to its own wire message.
/// </summary>
public class DbRecord<T> where T : class
{
    public DbRecord()
    {
        ChangeType = ChangeType.None;
    }

    /// <param name="entityKey">Entity key, formatted with <see cref="DbRecordKey.Format"/>.</param>
    public DbRecord(string entityKey, ChangeType changeType, List<string>? connections = null)
    {
        EntityKey = entityKey;
        ChangeType = changeType;
        Connections = connections;
    }

    public DbRecord(long rowId, ChangeType changeType, List<string>? connections = null)
        : this(DbRecordKey.Format(rowId), changeType, connections)
    {
    }

    /// <summary>Key of the changed entity, formatted with <see cref="DbRecordKey.Format"/>; read it back with <see cref="DbRecordKey.Parse{TKey}(string)"/>.</summary>
    public string EntityKey { get; set; } = string.Empty;

    public ChangeType ChangeType { get; set; }

    /// <summary>Connections already aware of the change (typically its author): they are not notified again.</summary>
    public List<string>? Connections { get; set; }

    public override string ToString()
        => $"{typeof(T).Name} : {EntityKey} {ChangeType} {string.Join(",", Connections ?? [])}";

    public void Add(string? connectionId)
    {
        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            Connections ??= [];
            Connections.Add(connectionId);
        }
    }

    public bool Contains(string? connectionId)
        => !string.IsNullOrWhiteSpace(connectionId) && (Connections ?? []).Contains(connectionId);
}

namespace Queryeables.Abstraction;

/// <summary>
/// A row change: which entity, what kind of change, and which clients already know about it.
/// Serializer-agnostic: each transport (MagicOnion, SignalR...) maps it to its own wire message.
/// </summary>
public class EntityChange<T> where T : class
{
    public EntityChange()
    {
        ChangeType = ChangeType.None;
    }

    /// <param name="entityKey">Entity key, formatted with <see cref="Queryeables.Abstraction.EntityKey.Format"/>.</param>
    public EntityChange(string entityKey, ChangeType changeType, List<string>? connections = null)
    {
        EntityKey = entityKey;
        ChangeType = changeType;
        Connections = connections;
    }

    public EntityChange(long rowId, ChangeType changeType, List<string>? connections = null)
        : this(Queryeables.Abstraction.EntityKey.Format(rowId), changeType, connections)
    {
    }

    /// <summary>Key of the changed entity, formatted with <see cref="Queryeables.Abstraction.EntityKey.Format"/>; read it back with <see cref="Queryeables.Abstraction.EntityKey.Parse{TKey}(string)"/>.</summary>
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

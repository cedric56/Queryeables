namespace Queryeables.Abstraction;

/// <summary>
/// A row change: which entity, and what kind of change.
/// Serializer-agnostic: each transport (MagicOnion, SignalR...) maps it to its own wire message.
/// </summary>
public class EntityChange<T> where T : class
{
    public EntityChange()
    {
        ChangeType = ChangeType.None;
    }

    /// <param name="entityKey">Entity key, formatted with <see cref="Queryeables.Abstraction.EntityKey.Format"/>.</param>
    public EntityChange(string entityKey, ChangeType changeType)
    {
        EntityKey = entityKey;
        ChangeType = changeType;
    }

    public EntityChange(long rowId, ChangeType changeType)
        : this(Queryeables.Abstraction.EntityKey.Format(rowId), changeType)
    {
    }

    /// <summary>Key of the changed entity, formatted with <see cref="Queryeables.Abstraction.EntityKey.Format"/>; read it back with <see cref="Queryeables.Abstraction.EntityKey.Parse{TKey}(string)"/>.</summary>
    public string EntityKey { get; set; } = string.Empty;

    public ChangeType ChangeType { get; set; }

    public override string ToString() => $"{typeof(T).Name} : {EntityKey} {ChangeType}";
}

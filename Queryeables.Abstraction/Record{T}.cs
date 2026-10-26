using MessagePack;
using System.Globalization;
using KeyAttribute = MessagePack.KeyAttribute;

namespace Queryeables.Abstraction;

/// <summary>Changement d'une ligne : quelle entité, quel type de changement, et quels clients en sont déjà informés.</summary>
[MessagePackObject]
public class Record<T> where T : class
{
    public Record()
    {
        ChangeType = ChangeType.None;
    }

    /// <param name="entityKey">Clé de l'entité au format de <see cref="RecordKey.Format"/>.</param>
    public Record(string entityKey, ChangeType changeType, List<string>? connections = null)
    {
        EntityKey = entityKey;
        ChangeType = changeType;
        Connections = connections;
    }

    public Record(long rowId, ChangeType changeType, List<string>? connections = null)
        : this(RecordKey.Format(rowId), changeType, connections)
    {
    }

    /// <summary>Clé de l'entité modifiée, au format de <see cref="RecordKey.Format"/> ; relue avec <see cref="RecordKey.Parse{TKey}"/>.</summary>
    [Key(0)]
    public string EntityKey { get; set; } = string.Empty;

    /// <summary>Ancienne clé numérique, pour le code qui l'utilise encore.</summary>
    [IgnoreMember]
    [Obsolete("Utilisez EntityKey avec RecordKey.Parse<TKey>.")]
    public long RowId => long.Parse(EntityKey, CultureInfo.InvariantCulture);

    [Key(1)]
    public ChangeType ChangeType { get; set; }

    [Key(2)]
    public List<string>? Connections { get; set; }

    public override string ToString()
    {
        return $"{typeof(T).Name} : {EntityKey} {ChangeType} {string.Join(",", Connections ?? [])}";
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

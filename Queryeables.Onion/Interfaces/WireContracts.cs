using MessagePack;

namespace Queryeables.Onion;

public enum FilterOperator
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,

    /// <summary>La propriété vaut l'une des valeurs (Value est une liste).</summary>
    In,

    /// <summary>Texte qui commence par Value.</summary>
    StartsWith,

    /// <summary>Texte qui se termine par Value.</summary>
    EndsWith,

    /// <summary>Texte qui contient Value.</summary>
    Contains,
}

/// <summary>Une condition « propriété opérateur valeur » transmise au serveur.</summary>
[MessagePackObject]
public sealed class FilterCondition
{
    public FilterCondition()
    {
    }

    public FilterCondition(string property, FilterOperator @operator, object? value)
    {
        Property = property;
        Operator = @operator;
        Value = value;
    }

    [Key(0)]
    public string Property { get; set; } = string.Empty;

    [Key(1)]
    public FilterOperator Operator { get; set; }

    [Key(2)]
    public object? Value { get; set; }

    public override string ToString() => $"{Property} {Operator} {Value}";
}

public enum BatchOperationKind
{
    Insert,
    Update,
    Delete,
}

/// <summary>Une écriture d'un lot exécuté dans une seule transaction côté serveur.</summary>
[MessagePackObject]
public sealed class BatchOperation
{
    public BatchOperation()
    {
    }

    public BatchOperation(string type, BatchOperationKind kind, byte[] entity, string? connectionId)
    {
        Type = type;
        Kind = kind;
        Entity = entity;
        ConnectionId = connectionId;
    }

    /// <summary>Nom du type d'entité (voir <see cref="EntityName"/>).</summary>
    [Key(0)]
    public string Type { get; set; } = string.Empty;

    [Key(1)]
    public BatchOperationKind Kind { get; set; }

    [Key(2)]
    public byte[] Entity { get; set; } = [];

    /// <summary>Session de notification du client pour ce type d'entité.</summary>
    [Key(3)]
    public string? ConnectionId { get; set; }
}

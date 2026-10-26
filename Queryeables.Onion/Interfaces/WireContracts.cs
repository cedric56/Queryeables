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

    /// <summary>The property equals one of the values (Value is a list).</summary>
    In,

    /// <summary>Text that starts with Value.</summary>
    StartsWith,

    /// <summary>Text that ends with Value.</summary>
    EndsWith,

    /// <summary>Text that contains Value.</summary>
    Contains,
}

/// <summary>A "property operator value" condition sent to the server.</summary>
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

/// <summary>One write of a batch executed in a single server-side transaction.</summary>
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

    /// <summary>Name of the entity type (see <see cref="EntityName"/>).</summary>
    [Key(0)]
    public string Type { get; set; } = string.Empty;

    [Key(1)]
    public BatchOperationKind Kind { get; set; }

    [Key(2)]
    public byte[] Entity { get; set; } = [];

    /// <summary>Notification session of the client for this entity type.</summary>
    [Key(3)]
    public string? ConnectionId { get; set; }
}

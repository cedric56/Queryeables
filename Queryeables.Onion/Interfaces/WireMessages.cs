using MessagePack;
using Queryeables.Remote;

namespace Queryeables.Onion;

/// <summary>MessagePack form of a <see cref="FilterCondition"/>.</summary>
[MessagePackObject]
public sealed class FilterConditionMessage
{
    [Key(0)]
    public string Property { get; set; } = string.Empty;

    [Key(1)]
    public FilterOperator Operator { get; set; }

    [Key(2)]
    public object? Value { get; set; }

    public static FilterConditionMessage From(FilterCondition condition)
        => new() { Property = condition.Property, Operator = condition.Operator, Value = condition.Value };

    public FilterCondition ToCondition() => new(Property, Operator, Value);

    public static FilterConditionMessage[] From(FilterCondition[]? conditions)
        => conditions is null ? [] : Array.ConvertAll(conditions, From);

    public static FilterCondition[] ToConditions(FilterConditionMessage[]? messages)
        => messages is null ? [] : Array.ConvertAll(messages, m => m.ToCondition());
}

/// <summary>MessagePack form of a <see cref="BatchOperation"/>.</summary>
[MessagePackObject]
public sealed class BatchOperationMessage
{
    [Key(0)]
    public string Type { get; set; } = string.Empty;

    [Key(1)]
    public BatchOperationKind Kind { get; set; }

    [Key(2)]
    public byte[] Entity { get; set; } = [];

    [Key(3)]
    public string? ConnectionId { get; set; }

    public static BatchOperationMessage From(BatchOperation operation)
        => new() { Type = operation.Type, Kind = operation.Kind, Entity = operation.Entity, ConnectionId = operation.ConnectionId };

    public BatchOperation ToOperation() => new(Type, Kind, Entity, ConnectionId);

    public static BatchOperationMessage[] From(BatchOperation[]? operations)
        => operations is null ? [] : Array.ConvertAll(operations, From);

    public static BatchOperation[] ToOperations(BatchOperationMessage[]? messages)
        => messages is null ? [] : Array.ConvertAll(messages, m => m.ToOperation());
}

/// <summary>MessagePack form of a <see cref="Queryeables.Abstraction.SortKey"/>.</summary>
[MessagePackObject]
public sealed class SortKeyMessage
{
    [Key(0)]
    public string Property { get; set; } = string.Empty;

    [Key(1)]
    public bool Descending { get; set; }

    public static SortKeyMessage[] From(Queryeables.Abstraction.SortKey[]? keys)
        => keys is null ? [] : Array.ConvertAll(keys, k => new SortKeyMessage { Property = k.Property, Descending = k.Descending });

    public static Queryeables.Abstraction.SortKey[] ToKeys(SortKeyMessage[]? messages)
        => messages is null ? [] : Array.ConvertAll(messages, m => new Queryeables.Abstraction.SortKey(m.Property, m.Descending));
}

using MessagePack;
using Queryeables.Remote;

namespace Queryeables.MagicOnion;

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

    public FilterCondition ToCondition() => new(Property ?? throw Malformed("property name"), Operator, Value);

    public static FilterConditionMessage[] From(FilterCondition[]? conditions)
        => conditions is null ? [] : Array.ConvertAll(conditions, From);

    /// <summary>A null element of an array: a malformed message (answered with InvalidRequest).</summary>
    internal static ArgumentException Malformed(string element) => new($"The message holds an empty {element}.");

    public static FilterCondition[] ToConditions(FilterConditionMessage[]? messages)
        => messages is null ? [] : Array.ConvertAll(messages, m => (m ?? throw Malformed("filter condition")).ToCondition());
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
        => messages is null ? [] : Array.ConvertAll(messages, m => (m ?? throw FilterConditionMessage.Malformed("batch operation")).ToOperation());
}

/// <summary>MessagePack form of a <see cref="Queryeables.Abstraction.SortKey"/>.</summary>
/// <summary>A <see cref="RemoteRequest"/> on the wire.</summary>
[MessagePackObject]
public sealed class RemoteRequestMessage
{
    [Key(0)]
    public RemoteRequestKind Kind { get; set; }

    [Key(1)]
    public FilterConditionMessage[] Filter { get; set; } = [];

    [Key(2)]
    public bool Any { get; set; }

    [Key(3)]
    public FilterConditionMessage[] Values { get; set; } = [];

    [Key(4)]
    public string[] Members { get; set; } = [];

    [Key(5)]
    public byte[]? Original { get; set; }

    [Key(6)]
    public byte[]? Entity { get; set; }

    [Key(7)]
    public string? ConnectionId { get; set; }

    public static RemoteRequestMessage From(RemoteRequest request) => new()
    {
        Kind = request.Kind,
        Filter = FilterConditionMessage.From(request.Filter),
        Any = request.Any,
        Values = FilterConditionMessage.From(request.Values),
        Members = request.Members ?? [],
        Original = request.Original,
        Entity = request.Entity,
        ConnectionId = request.ConnectionId,
    };

    public RemoteRequest ToRequest() => new()
    {
        Kind = Kind,
        Filter = FilterConditionMessage.ToConditions(Filter),
        Any = Any,
        Values = FilterConditionMessage.ToConditions(Values),
        Members = Members is null ? [] : Array.TrueForAll(Members, m => m is not null) ? Members : throw FilterConditionMessage.Malformed("member"),
        Original = Original,
        Entity = Entity,
        ConnectionId = ConnectionId,
    };
}

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
        => messages is null ? [] : Array.ConvertAll(messages, m => m is null
            ? throw FilterConditionMessage.Malformed("sort key")
            : new Queryeables.Abstraction.SortKey(m.Property, m.Descending));
}

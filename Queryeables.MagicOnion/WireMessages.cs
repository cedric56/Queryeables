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


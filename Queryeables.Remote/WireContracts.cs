namespace Queryeables.Remote;

/// <summary>The comparison of a <see cref="FilterCondition"/>: a row is read by key, so equality only.</summary>
public enum FilterOperator
{
    Equal,
}

/// <summary>A "property = value" condition sent to the server: one per column of the key of the row to read.</summary>
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

    public string Property { get; set; } = string.Empty;

    public FilterOperator Operator { get; set; }

    public object? Value { get; set; }

    public override string ToString() => $"{Property} {Operator} {Value}";
}

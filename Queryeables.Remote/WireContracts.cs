
namespace Queryeables.Remote;

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

    /// <summary>
    /// Postfix node: all of the previous <c>Value</c> conditions (a number). Property is empty. A filter with such nodes is
    /// read in postfix order and ignores <c>Any</c>.
    /// </summary>
    And,

    /// <summary>Postfix node: one of the previous <c>Value</c> conditions (a number). See <see cref="And"/>.</summary>
    Or,

    // ---------- The extended form (FilterLanguage): values and operations on them, in postfix ----------

    /// <summary>A value: the property at the path in <see cref="FilterCondition.Property"/> (<c>Customer.City</c>).</summary>
    Member,

    /// <summary>A value: the constant in <see cref="FilterCondition.Value"/>.</summary>
    Value,

    /// <summary>The sum of the two values before it.</summary>
    Add,

    Subtract,

    Multiply,

    Divide,

    Modulo,

    /// <summary>The function named in <see cref="FilterCondition.Property"/>, of the values before it (their number in <see cref="FilterCondition.Value"/>).</summary>
    Function,

    /// <summary>The negation of the condition before it.</summary>
    Not,

    /// <summary>
    /// A row of the collection at the path in <see cref="FilterCondition.Property"/> exists: matching the condition before it
    /// when <see cref="FilterCondition.Value"/> is 1, any when 0.
    /// </summary>
    Any,

    /// <summary>Every row of the collection matches the condition before it.</summary>
    All,

    /// <summary>The number of rows of the collection (matching the condition before it when <see cref="FilterCondition.Value"/> is 1).</summary>
    Count,

    /// <summary>The first value before it when not null, the second otherwise.</summary>
    Coalesce,

    /// <summary>An aggregation: the value before it is the key of the groups.</summary>
    GroupKey,

    /// <summary>An aggregation: the function named in <see cref="FilterCondition.Property"/>, of the value before it (none for Count).</summary>
    Aggregate,
}

/// <summary>A "property operator value" condition sent to the server.</summary>
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

public enum BatchOperationKind
{
    Insert,
    Update,
    Delete,
}

/// <summary>One write of a batch executed in a single server-side transaction.</summary>
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
    public string Type { get; set; } = string.Empty;

    public BatchOperationKind Kind { get; set; }

    public byte[] Entity { get; set; } = [];

    /// <summary>Notification session of the client for this entity type.</summary>
    public string? ConnectionId { get; set; }
}

/// <summary>The operations of <see cref="IEntityTransport.ExecuteAsync"/>, beyond reading and writing rows one by one.</summary>
public enum RemoteRequestKind
{
    /// <summary>The number of matching rows (a long).</summary>
    Count,

    /// <summary>
    /// Values over the matching rows, by group (<see cref="RemoteRequest.Values"/>: the key, then each value, postfix with
    /// their markers). An array of rows, each <c>[key, count, value1, ...]</c>.
    /// </summary>
    Aggregate,

    /// <summary>The matching rows with only the properties in <see cref="RemoteRequest.Members"/> (the others at their default).</summary>
    Project,

    /// <summary>
    /// The properties in <see cref="RemoteRequest.Members"/> written over the stored row, from <see cref="RemoteRequest.Original"/>
    /// to <see cref="RemoteRequest.Entity"/>: a conflict only when one of them changed meanwhile. The stored row, or nothing.
    /// </summary>
    Patch,

    /// <summary>The values in <see cref="RemoteRequest.Values"/> (property, value) assigned to every matching row: their number (an int).</summary>
    UpdateWhere,

    /// <summary>Every matching row deleted: their number (an int).</summary>
    DeleteWhere,

    /// <summary>
    /// An update of a graph: <see cref="RemoteRequest.Entity"/>, its children as the client holds them, and in
    /// <see cref="RemoteRequest.Members"/> the ids of those it left as read (their rows are left alone). As an update.
    /// </summary>
    SaveGraph,
}

/// <summary>One request of <see cref="IEntityTransport.ExecuteAsync"/>: what it does, on which rows, with what.</summary>
public sealed class RemoteRequest
{
    public RemoteRequestKind Kind { get; set; }

    /// <summary>The rows: a filter, as <see cref="IEntityTransport.SelectAsync"/> takes it.</summary>
    public FilterCondition[] Filter { get; set; } = [];

    /// <summary>The conditions of <see cref="Filter"/> are combined with "or" (the flat form).</summary>
    public bool Any { get; set; }

    /// <summary>The values of an aggregation, or the assignments of an update (property, value).</summary>
    public FilterCondition[] Values { get; set; } = [];

    /// <summary>Names: the properties of a projection or a patch, the children left unchanged in a graph.</summary>
    public string[] Members { get; set; } = [];

    /// <summary>The entity as read (a patch).</summary>
    public byte[]? Original { get; set; }

    /// <summary>The entity as written (a patch, a graph).</summary>
    public byte[]? Entity { get; set; }

    /// <summary>The client's notification connection, so that it is not told about its own write.</summary>
    public string? ConnectionId { get; set; }
}

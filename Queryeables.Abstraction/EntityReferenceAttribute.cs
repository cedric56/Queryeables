namespace Queryeables.Abstraction;

/// <summary>
/// Marks a navigation to another entity, held by a foreign key of this one (an order and its customer). Filters may then go
/// through it (<c>o =&gt; o.Customer!.City == "Paris"</c>, also remotely), loading a list sets it with one query for the
/// whole list, and a row created offline under a temporary key is referred to by its real key once the server gave it.
/// </summary>
/// <example>
/// <code>
/// public class Order
/// {
///     public long Id { get; set; }
///     public long CustomerId { get; set; }
///
///     [EntityReference]                       // ForeignKey: CustomerId or IdCustomer by default
///     public Customer? Customer { get; set; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EntityReferenceAttribute : Attribute
{
    /// <summary>The property of this entity holding the key of the other. By default <c>{Navigation}Id</c> or <c>Id{Navigation}</c>.</summary>
    public string? ForeignKey { get; set; }
}

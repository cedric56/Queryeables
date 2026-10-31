namespace Queryeables.Abstraction;

/// <summary>
/// Marks a collection of child entities: the parent is saved, removed and loaded with its children. The source generator
/// writes <c>{Parent}Graph</c> and <c>{Parent}Repository</c> (an <see cref="IAggregateRepository{TEntity}"/>), registered by
/// <c>AddGeneratedEntities()</c>.
/// </summary>
/// <example>
/// <code>
/// public class Person
/// {
///     public long Id { get; set; }
///
///     // Each contact belongs to a person (IdContact) and points to another person (IdPerson).
///     [EntityGraph(ForeignKey = nameof(Contact.IdContact), ParentNavigation = nameof(Contact.Owner),
///                  LookupNavigation = nameof(Contact.Person), LookupForeignKey = nameof(Contact.IdPerson))]
///     public List&lt;Contact&gt; Contacts { get; set; } = [];
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EntityGraphAttribute : Attribute
{
    /// <summary>
    /// Child property holding the key of the parent. By default <c>Id{Parent}</c> or <c>{Parent}Id</c>.
    /// </summary>
    public string? ForeignKey { get; set; }

    /// <summary>Child property set to the parent after loading (optional).</summary>
    public string? ParentNavigation { get; set; }

    /// <summary>
    /// Child property set, after loading, to another entity found in its cache (optional): for example the product of an
    /// order line. The cache of that type is registered and given to the generated graph.
    /// </summary>
    public string? LookupNavigation { get; set; }

    /// <summary>
    /// Child property holding the key of the entity of <see cref="LookupNavigation"/>.
    /// By default <c>Id{LookupNavigation}</c> or <c>{LookupNavigation}Id</c>.
    /// </summary>
    public string? LookupForeignKey { get; set; }
}

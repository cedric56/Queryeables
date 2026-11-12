namespace Queryeables.Abstraction;

/// <summary>
/// Marks the children of an entity: the parent is saved, removed and loaded with them. On a collection, its rows (one to
/// many); on a property of an entity type, one child (one to one, its foreign key toward the parent); with
/// <see cref="Through"/>, other entities linked through a table of links (many to many). A child type that has
/// <c>[EntityGraph]</c> children too is saved, removed and loaded with them: a graph has as many levels as its types. The source generator
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

    /// <summary>
    /// One child: a null property deletes the stored child when true. False by default: null then means "not loaded", as
    /// for a collection, and the stored child stays.
    /// </summary>
    public bool NullDeletes { get; set; }

    /// <summary>
    /// Many to many: the entity type of the links (a product and its tags through <c>ProductTag</c>). The collection holds the
    /// linked entities, which are never written: saving aligns the links with it. Both sides may declare it.
    /// </summary>
    public Type? Through { get; set; }

    /// <summary>Property of the link holding the key of this entity. By default <c>{Parent}Id</c> or <c>Id{Parent}</c>.</summary>
    public string? ThroughForeignKey { get; set; }

    /// <summary>Property of the link holding the key of the linked entity. By default <c>{Target}Id</c> or <c>Id{Target}</c>.</summary>
    public string? ThroughTargetKey { get; set; }
}

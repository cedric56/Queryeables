using Queryeables.Abstraction;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Sample;

/// <summary>A person and its contacts. [EntityCache]: an in-memory cache that follows the database.</summary>
[EntityCache]
[Table("Person")]
public class Person
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Saved, removed and loaded with the person. Each contact belongs to its owner (OwnerId) and points to another person
    /// (PersonId), found in the cache of persons after loading. Null until loaded: saving a person whose contacts were not
    /// loaded leaves them as they are, while an empty list deletes them.
    /// </summary>
    [NotMapped]
    [EntityGraph(ForeignKey = nameof(Contact.OwnerId), ParentNavigation = nameof(Contact.Owner),
        LookupNavigation = nameof(Contact.Person), LookupForeignKey = nameof(Contact.PersonId))]
    public List<Contact>? Contacts { get; set; }

    public override string ToString() => Name;
}

[Table("Contact")]
public class Contact
{
    public long Id { get; set; }

    public long OwnerId { get; set; }

    public long? PersonId { get; set; }

    public string Kind { get; set; } = string.Empty;

    // Navigation properties: not columns, and not sent over the wire (the owner points back to its contacts).
    [NotMapped, JsonIgnore]
    public Person? Owner { get; set; }

    [NotMapped, JsonIgnore]
    public Person? Person { get; set; }
}

namespace Sample;

/// <summary>A person and its contacts: the tables Person and Contact. [EntityCache]: an in-memory cache that follows the database.</summary>
[EntityCache]
public class Person
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Saved, removed and loaded with the person. Each contact belongs to its owner (OwnerId) and points to another person
    /// (PersonId), found in the cache of persons after loading. Null until loaded: saving a person whose contacts were not
    /// loaded leaves them as they are, while an empty list deletes them.
    /// </summary>
    [EntityGraph(ForeignKey = nameof(Contact.OwnerId), ParentNavigation = nameof(Contact.Owner),
        LookupNavigation = nameof(Contact.Person), LookupForeignKey = nameof(Contact.PersonId))]
    public List<Contact>? Contacts { get; set; }

    public override string ToString() => Name;
}

public class Contact
{
    public long Id { get; set; }

    public long OwnerId { get; set; }

    public long? PersonId { get; set; }

    public string Kind { get; set; } = string.Empty;

    // Navigation properties: not columns; on the wire, the owner is written once (JSON cuts the cycle).
    public Person? Owner { get; set; }

    public Person? Person { get; set; }
}

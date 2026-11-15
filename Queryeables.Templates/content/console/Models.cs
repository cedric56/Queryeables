namespace QueryeablesApp;

/// <summary>A person and its contacts: a table each (Person, Contact). [EntityCache]: an in-memory cache that follows the table.</summary>
[EntityCache(Preload = true)]
public class Person
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Saved, loaded and removed with the person. Null: not loaded (saving leaves them as they are).</summary>
    [EntityGraph(ParentNavigation = nameof(Contact.Owner))]
    public List<Contact>? Contacts { get; set; }

    public override string ToString() => Name;
}

public class Contact
{
    public long Id { get; set; }

    public long PersonId { get; set; }             // the parent, found by its name: {Parent}Id

    public string Kind { get; set; } = "";

    public Person? Owner { get; set; }             // a navigation, not a column
}

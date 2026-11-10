using Queryeables.Abstraction;
using System.ComponentModel.DataAnnotations.Schema;

namespace QueryeablesApp;

/// <summary>A person and its contacts. [EntityCache]: an in-memory cache that follows the table.</summary>
[EntityCache]
[Table("Person")]                     // Dommel pluralizes table names by default
public class Person
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Saved, loaded and removed with the person. Null: not loaded (saving leaves them as they are).</summary>
    [NotMapped]
    [EntityGraph(ParentNavigation = nameof(Contact.Owner))]
    public List<Contact>? Contacts { get; set; }

    public override string ToString() => Name;
}

[Table("Contact")]
public class Contact
{
    public long Id { get; set; }

    public long PersonId { get; set; }             // the parent, found by its name: {Parent}Id

    public string Kind { get; set; } = "";

    [NotMapped]                                    // not a column
    public Person? Owner { get; set; }
}

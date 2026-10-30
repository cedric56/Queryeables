using Queryeables.Abstraction;
using System.Text.Json.Serialization;

namespace Queryeables.AotSmoke;

public interface IHasId
{
    long Id { get; set; }
}

[EntityCache]
public sealed class Person : IHasId
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Active { get; set; }

    [EntityGraph(ParentNavigation = nameof(Contact.Owner))]
    public List<Contact>? Contacts { get; set; }
}

public sealed class Contact : IHasId
{
    public long Id { get; set; }
    public long PersonId { get; set; }
    public string Kind { get; set; } = string.Empty;

    [JsonIgnore]
    public Person? Owner { get; set; }
}

/// <summary>Source-generated JSON: no reflection-based serialization under Native AOT.</summary>
[JsonSerializable(typeof(Person))]
[JsonSerializable(typeof(Person[]))]
[JsonSerializable(typeof(Contact))]
[JsonSerializable(typeof(Contact[]))]
[JsonSerializable(typeof(byte[][]))]
[JsonSerializable(typeof(bool))]
internal sealed partial class SmokeJson : JsonSerializerContext;

using Queryeables.Abstraction;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Queryeables.Tests.Unit;

/// <summary>A person with two collections of children, for the graph tests.</summary>
public sealed class Member
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Not carried by the serializer.</summary>
    [JsonIgnore]
    public string LocalTag { get; set; } = string.Empty;

    public List<MemberContact>? Contacts { get; set; }
    public List<MemberNote>? Notes { get; set; }
}

public sealed class MemberContact
{
    public long Id { get; set; }
    public long PersonId { get; set; }
    public string Label { get; set; } = string.Empty;

    [JsonIgnore]
    public Member? Owner { get; set; }
}

public sealed class MemberNote
{
    public long Id { get; set; }
    public long PersonId { get; set; }
    public string Text { get; set; } = string.Empty;
}

[Table("Named_Rows")]
public sealed class NamedTable
{
    public long Id { get; set; }
}

public record NameTag(string Name);

/// <summary>An account whose hash never leaves the server.</summary>
public sealed class Account
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    [JsonIgnore] public string PasswordHash { get; set; } = string.Empty;
    public string? Role { get; set; }
}

/// <summary>An order and its lines: the price of a line is the server's.</summary>
public sealed class Invoice
{
    public long Id { get; set; }
    public string Customer { get; set; } = string.Empty;
    public List<InvoiceLine>? Lines { get; set; }
}

public sealed class InvoiceLine
{
    public long Id { get; set; }
    public long InvoiceId { get; set; }
    public string Product { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public string Owner { get; set; } = string.Empty;
}

/// <summary>A folder and its notes: a graph whose children have rules of their own.</summary>
public class ValidatedFolder
{
    public long Id { get; set; }
    [Required] public string Name { get; set; } = string.Empty;
    [EntityGraph] public List<ValidatedNote> Notes { get; set; } = [];
}

public class ValidatedNote
{
    public long Id { get; set; }
    public long ValidatedFolderId { get; set; }
    [Required, StringLength(5)] public string Text { get; set; } = string.Empty;
}

using MessagePack;
using Queryeables.Abstraction;
using System.ComponentModel.DataAnnotations.Schema;
using KeyAttribute = System.ComponentModel.DataAnnotations.KeyAttribute;

namespace Queryeables.Tests;

/// <summary>Entity shared by all tests: usable by Dommel, EF and MessagePack.</summary>
[Table("Clients")]
[MessagePackObject(keyAsPropertyName: true)]
public class TestClient
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [System.ComponentModel.DataAnnotations.Required(AllowEmptyStrings = false, ErrorMessage = "The name is required.")]
    [System.ComponentModel.DataAnnotations.StringLength(50, ErrorMessage = "The name is at most 50 characters long.")]
    public string Name { get; set; } = string.Empty;

    public bool Active { get; set; }

    public int? SponsorId { get; set; }

    /// <summary>Version for optimistic concurrency, managed by the stores.</summary>
    [System.ComponentModel.DataAnnotations.ConcurrencyCheck]
    public long Version { get; set; }

    /// <summary>Kept up to date by <see cref="SyncConventionsDecorator"/>.</summary>
    [LastModified]
    public DateTime LastModified { get; set; }

    /// <summary>Soft delete, applied by <see cref="SyncConventionsDecorator"/>.</summary>
    [SoftDelete]
    public bool Deleted { get; set; }

    public TestClient Copy() => new()
    {
        Id = Id, Name = Name, Active = Active, SponsorId = SponsorId, Version = Version, LastModified = LastModified, Deleted = Deleted
    };
}

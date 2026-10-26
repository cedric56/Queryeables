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

    [System.ComponentModel.DataAnnotations.Required(AllowEmptyStrings = false, ErrorMessage = "Le nom est obligatoire.")]
    [System.ComponentModel.DataAnnotations.StringLength(50, ErrorMessage = "Le nom fait au plus 50 caractères.")]
    public string Nom { get; set; } = string.Empty;

    public bool Actif { get; set; }

    public int? Parrain { get; set; }

    /// <summary>Version for optimistic concurrency, managed by the stores.</summary>
    [System.ComponentModel.DataAnnotations.ConcurrencyCheck]
    public long Version { get; set; }

    /// <summary>Kept up to date by <see cref="SyncConventionsStore"/>.</summary>
    [LastModified]
    public DateTime LastModified { get; set; }

    /// <summary>Soft delete, applied by <see cref="SyncConventionsStore"/>.</summary>
    [SoftDelete]
    public bool Deleted { get; set; }

    public TestClient Copy() => new()
    {
        Id = Id, Nom = Nom, Actif = Actif, Parrain = Parrain, Version = Version, LastModified = LastModified, Deleted = Deleted
    };
}

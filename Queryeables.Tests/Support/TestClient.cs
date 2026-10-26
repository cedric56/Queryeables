using MessagePack;
using System.ComponentModel.DataAnnotations.Schema;
using KeyAttribute = System.ComponentModel.DataAnnotations.KeyAttribute;

namespace Queryeables.Tests;

/// <summary>Entité commune à tous les tests : utilisable par Dommel, EF et MessagePack.</summary>
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

    /// <summary>Version pour la concurrence optimiste, gérée par les stores.</summary>
    [System.ComponentModel.DataAnnotations.ConcurrencyCheck]
    public long Version { get; set; }

    public TestClient Copy() => new() { Id = Id, Nom = Nom, Actif = Actif, Parrain = Parrain, Version = Version };
}

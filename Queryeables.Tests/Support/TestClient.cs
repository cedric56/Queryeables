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

    public string Nom { get; set; } = string.Empty;

    public bool Actif { get; set; }

    public int? Parrain { get; set; }

    public TestClient Copy() => new() { Id = Id, Nom = Nom, Actif = Actif, Parrain = Parrain };
}

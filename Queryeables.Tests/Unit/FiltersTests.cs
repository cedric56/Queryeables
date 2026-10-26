using Queryeables.Onion;
using System.Linq.Expressions;

namespace Queryeables.Tests.Unit;

public class FiltersTests
{
    public enum Statut { Inactif = 0, Actif = 1 }

    public class Row
    {
        public long Id { get; set; }
        public string Nom { get; set; } = "";
        public Statut Statut { get; set; }
        public bool Actif { get; set; }
        public int? Parrain { get; set; }
        public Guid Ref { get; set; }
    }

    private static readonly Row[] Rows =
    [
        new() { Id = 1, Nom = "Ana", Statut = Statut.Actif, Actif = true, Parrain = null, Ref = Guid.Parse("11111111-1111-1111-1111-111111111111") },
        new() { Id = 2, Nom = "Bob", Statut = Statut.Inactif, Actif = false, Parrain = 1, Ref = Guid.Empty },
        new() { Id = 5, Nom = "Cid", Statut = Statut.Actif, Actif = true, Parrain = 1, Ref = Guid.Empty },
    ];

    /// <summary>Simule MessagePack : les petits nombres reviennent en byte, les listes en tableau.</summary>
    private static Dictionary<string, object?> OverTheWire(Dictionary<string, object?> filters)
    {
        static object? Shrink(object? v) => v is long l && l is >= 0 and <= 255 ? (byte)l : v;
        return filters.ToDictionary(kv => kv.Key,
            kv => kv.Value is List<object?> list ? list.Select(Shrink).ToArray() : Shrink(kv.Value));
    }

    /// <summary>Le prédicat traduit, transporté puis reconstruit doit sélectionner les mêmes lignes.</summary>
    private static void AssertRoundTrip(Expression<Func<Row, bool>> predicate)
    {
        var (filters, any) = Filters.FromPredicate(predicate);
        var rebuilt = Filters.ToPredicate<Row>(OverTheWire(filters), any).Compile();

        Assert.Equal(Rows.Where(predicate.Compile()).Select(r => r.Id), Rows.Where(rebuilt).Select(r => r.Id));
    }

    [Fact]
    public void Captured_value() { long id = 5; AssertRoundTrip(c => c.Id == id); }

    [Fact]
    public void Constant_on_the_left() => AssertRoundTrip(c => 2 == c.Id);

    [Fact]
    public void Enum_value() => AssertRoundTrip(c => c.Statut == Statut.Actif);

    [Fact]
    public void And_with_boolean_property() => AssertRoundTrip(c => c.Actif && c.Parrain == 1);

    [Fact]
    public void Negated_boolean() => AssertRoundTrip(c => !c.Actif);

    [Fact]
    public void Nullable_compared_to_null() => AssertRoundTrip(c => c.Parrain == null);

    [Fact]
    public void Guid_value() => AssertRoundTrip(c => c.Ref == Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public void Or_on_the_same_property() => AssertRoundTrip(c => c.Nom == "Ana" || c.Nom == "Bob");

    [Fact]
    public void Or_with_three_values() => AssertRoundTrip(c => c.Id == 1 || c.Id == 2 || c.Id == 5);

    [Fact]
    public void Or_on_different_properties() => AssertRoundTrip(c => c.Nom == "Bob" || c.Id == 5);

    [Fact]
    public void Always_true_selects_everything() => AssertRoundTrip(c => true);

    [Fact]
    public void Comparison_is_rejected()
        => Assert.Throws<NotSupportedException>(() => Filters.FromPredicate<Row>(c => c.Id > 1));

    [Fact]
    public void Mixed_and_or_is_rejected()
        => Assert.Throws<NotSupportedException>(() => Filters.FromPredicate<Row>(c => c.Id == 1 && (c.Nom == "a" || c.Nom == "b")));

    [Fact]
    public void Same_property_twice_in_and_is_rejected()
        => Assert.Throws<NotSupportedException>(() => Filters.FromPredicate<Row>(c => c.Id == 1 && c.Id == 2));

    [Fact]
    public void Method_call_is_rejected()
        => Assert.Throws<NotSupportedException>(() => Filters.FromPredicate<Row>(c => c.Nom.StartsWith("A")));

    [Fact]
    public void Null_for_a_non_nullable_property_is_rejected()
        => Assert.Throws<ArgumentException>(() => Filters.ToPredicate<Row>(new Dictionary<string, object?> { ["Id"] = null }, false));

    [Fact]
    public void Unknown_property_is_rejected()
        => Assert.Throws<ArgumentException>(() => Filters.ToPredicate<Row>(new Dictionary<string, object?> { ["Inconnue"] = 1 }, false));
}

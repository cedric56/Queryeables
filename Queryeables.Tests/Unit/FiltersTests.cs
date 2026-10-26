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
        public DateTime Creation { get; set; }
    }

    private static readonly Row[] Rows =
    [
        new() { Id = 1, Nom = "Ana", Statut = Statut.Actif, Actif = true, Parrain = null, Ref = Guid.Parse("11111111-1111-1111-1111-111111111111"), Creation = new DateTime(2026, 1, 1) },
        new() { Id = 2, Nom = "Bob", Statut = Statut.Inactif, Actif = false, Parrain = 1, Ref = Guid.Empty, Creation = new DateTime(2026, 6, 1) },
        new() { Id = 5, Nom = "Annabelle", Statut = Statut.Actif, Actif = true, Parrain = 1, Ref = Guid.Empty, Creation = new DateTime(2026, 9, 1) },
    ];

    /// <summary>Simule MessagePack : les petits nombres reviennent en byte, y compris dans les listes.</summary>
    private static FilterCondition[] OverTheWire(FilterCondition[] conditions)
    {
        static object? Shrink(object? v) => v is long l && l is >= 0 and <= 255 ? (byte)l : v;
        return conditions.Select(c => new FilterCondition(c.Property, c.Operator,
            c.Value is object?[] list ? list.Select(Shrink).ToArray() : Shrink(c.Value))).ToArray();
    }

    /// <summary>Le prédicat traduit, transporté puis reconstruit doit sélectionner les mêmes lignes.</summary>
    private static void AssertRoundTrip(Expression<Func<Row, bool>> predicate)
    {
        var (conditions, any) = Filters.FromPredicate(predicate);
        var rebuilt = Filters.ToPredicate<Row>(OverTheWire(conditions), any).Compile();

        Assert.Equal(Rows.Where(predicate.Compile()).Select(r => r.Id), Rows.Where(rebuilt).Select(r => r.Id));
    }

    // ---------- Égalités et booléens ----------

    [Fact] public void Captured_value() { long id = 5; AssertRoundTrip(c => c.Id == id); }
    [Fact] public void Constant_on_the_left() => AssertRoundTrip(c => 2 == c.Id);
    [Fact] public void Enum_value() => AssertRoundTrip(c => c.Statut == Statut.Actif);
    [Fact] public void And_with_boolean_property() => AssertRoundTrip(c => c.Actif && c.Parrain == 1);
    [Fact] public void Negated_boolean() => AssertRoundTrip(c => !c.Actif);
    [Fact] public void Nullable_compared_to_null() => AssertRoundTrip(c => c.Parrain == null);
    [Fact] public void Guid_value() => AssertRoundTrip(c => c.Ref == Guid.Parse("11111111-1111-1111-1111-111111111111"));
    [Fact] public void Or_on_the_same_property() => AssertRoundTrip(c => c.Nom == "Ana" || c.Nom == "Bob");
    [Fact] public void Or_on_different_properties() => AssertRoundTrip(c => c.Nom == "Bob" || c.Id == 5);
    [Fact] public void Always_true_selects_everything() => AssertRoundTrip(c => true);

    // ---------- Comparaisons ----------

    [Fact] public void Not_equal() => AssertRoundTrip(c => c.Id != 2);
    [Fact] public void Greater_than() => AssertRoundTrip(c => c.Id > 1);
    [Fact] public void Constant_on_the_left_is_mirrored() => AssertRoundTrip(c => 2 < c.Id);
    [Fact] public void Range_on_the_same_property() => AssertRoundTrip(c => c.Id >= 2 && c.Id <= 5);
    [Fact] public void Nullable_comparison() => AssertRoundTrip(c => c.Parrain >= 1);
    [Fact] public void Enum_comparison() => AssertRoundTrip(c => c.Statut > Statut.Inactif);
    [Fact] public void Date_comparison() => AssertRoundTrip(c => c.Creation < new DateTime(2026, 7, 1));

    // ---------- Listes et texte ----------

    [Fact] public void List_contains() { var ids = new List<long> { 1, 5 }; AssertRoundTrip(c => ids.Contains(c.Id)); }
    [Fact] public void Array_contains() { long[] ids = [2, 5]; AssertRoundTrip(c => ids.Contains(c.Id)); }
    [Fact] public void Enumerable_contains() { IEnumerable<string> noms = ["Bob"]; AssertRoundTrip(c => noms.Contains(c.Nom)); }
    [Fact] public void Empty_list_selects_nothing() { var ids = new List<long>(); AssertRoundTrip(c => ids.Contains(c.Id)); }
    [Fact] public void Starts_with() => AssertRoundTrip(c => c.Nom.StartsWith("An"));
    [Fact] public void Ends_with() => AssertRoundTrip(c => c.Nom.EndsWith("belle"));
    [Fact] public void Text_contains() => AssertRoundTrip(c => c.Nom.Contains("o"));

    // ---------- Refus côté client ----------

    [Fact]
    public void Mixed_and_or_is_rejected()
        => Assert.Throws<NotSupportedException>(() => Filters.FromPredicate<Row>(c => c.Id == 1 && (c.Nom == "a" || c.Nom == "b")));

    [Fact]
    public void Comparing_two_properties_is_rejected()
        => Assert.Throws<NotSupportedException>(() => Filters.FromPredicate<Row>(c => c.Id > c.Parrain));

    [Fact]
    public void Other_method_calls_are_rejected()
        => Assert.Throws<NotSupportedException>(() => Filters.FromPredicate<Row>(c => c.Nom.ToUpper() == "ANA"));

    [Fact]
    public void Nested_member_is_rejected()
        => Assert.Throws<NotSupportedException>(() => Filters.FromPredicate<Row>(c => c.Nom.Length > 3));

    [Fact]
    public void Text_value_containing_a_property_is_not_a_list()
        => Assert.Throws<NotSupportedException>(() => Filters.FromPredicate<Row>(c => "Ana Bob".Contains(c.Nom)));

    // ---------- Refus côté serveur (requête forgée) ----------

    private static void AssertServerRejects(params FilterCondition[] conditions)
        => Assert.Throws<ArgumentException>(() => Filters.ToPredicate<Row>(conditions, false));

    [Fact] public void Null_for_a_non_nullable_property_is_rejected() => AssertServerRejects(new FilterCondition("Id", FilterOperator.Equal, null));
    [Fact] public void Unknown_property_is_rejected() => AssertServerRejects(new FilterCondition("Inconnue", FilterOperator.Equal, 1));
    [Fact] public void Text_ordering_is_rejected() => AssertServerRejects(new FilterCondition("Nom", FilterOperator.GreaterThan, "A"));
    [Fact] public void StartsWith_on_a_number_is_rejected() => AssertServerRejects(new FilterCondition("Id", FilterOperator.StartsWith, "1"));
    [Fact] public void In_without_a_list_is_rejected() => AssertServerRejects(new FilterCondition("Id", FilterOperator.In, 1));
    [Fact] public void List_with_equal_is_rejected() => AssertServerRejects(new FilterCondition("Id", FilterOperator.Equal, new object[] { 1, 2 }));
    [Fact] public void Wrong_value_type_is_rejected() => AssertServerRejects(new FilterCondition("Id", FilterOperator.Equal, "pas un nombre"));
}

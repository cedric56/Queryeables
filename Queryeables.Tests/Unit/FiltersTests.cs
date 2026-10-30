using Queryeables.Remote;
using System.Linq.Expressions;

namespace Queryeables.Tests.Unit;

public class FiltersTests
{
    public enum Status { Inactive = 0, Active = 1 }

    public class Row
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public Status Status { get; set; }
        public bool Active { get; set; }
        public int? SponsorId { get; set; }
        public Guid Ref { get; set; }
        public DateTime Creation { get; set; }
    }

    private static readonly Row[] Rows =
    [
        new() { Id = 1, Name = "Ana", Status = Status.Active, Active = true, SponsorId = null, Ref = Guid.Parse("11111111-1111-1111-1111-111111111111"), Creation = new DateTime(2026, 1, 1) },
        new() { Id = 2, Name = "Bob", Status = Status.Inactive, Active = false, SponsorId = 1, Ref = Guid.Empty, Creation = new DateTime(2026, 6, 1) },
        new() { Id = 5, Name = "Annabelle", Status = Status.Active, Active = true, SponsorId = 1, Ref = Guid.Empty, Creation = new DateTime(2026, 9, 1) },
    ];

    /// <summary>Simulates MessagePack: small numbers come back as bytes, including in lists.</summary>
    private static FilterCondition[] OverTheWire(FilterCondition[] conditions)
    {
        static object? Shrink(object? v) => v is long l && l is >= 0 and <= 255 ? (byte)l : v;
        return conditions.Select(c => new FilterCondition(c.Property, c.Operator,
            c.Value is object?[] list ? list.Select(Shrink).ToArray() : Shrink(c.Value))).ToArray();
    }

    /// <summary>The translated, carried then rebuilt predicate must select the same rows.</summary>
    private static void AssertRoundTrip(Expression<Func<Row, bool>> predicate)
    {
        var (conditions, any) = Filters.FromPredicate(predicate);
        var rebuilt = Filters.ToPredicate<Row>(OverTheWire(conditions), any).Compile();

        Assert.Equal(Rows.Where(predicate.Compile()).Select(r => r.Id), Rows.Where(rebuilt).Select(r => r.Id));
    }

    // ---------- Equalities and booleans ----------

    [Fact] public void Captured_value() { long id = 5; AssertRoundTrip(c => c.Id == id); }
    [Fact] public void Constant_on_the_left() => AssertRoundTrip(c => 2 == c.Id);
    [Fact] public void Enum_value() => AssertRoundTrip(c => c.Status == Status.Active);
    [Fact] public void And_with_boolean_property() => AssertRoundTrip(c => c.Active && c.SponsorId == 1);
    [Fact] public void Negated_boolean() => AssertRoundTrip(c => !c.Active);
    [Fact] public void Nullable_compared_to_null() => AssertRoundTrip(c => c.SponsorId == null);
    [Fact] public void Guid_value() => AssertRoundTrip(c => c.Ref == Guid.Parse("11111111-1111-1111-1111-111111111111"));
    [Fact] public void Or_on_the_same_property() => AssertRoundTrip(c => c.Name == "Ana" || c.Name == "Bob");
    [Fact] public void Or_on_different_properties() => AssertRoundTrip(c => c.Name == "Bob" || c.Id == 5);
    [Fact] public void Always_true_selects_everything() => AssertRoundTrip(c => true);

    // ---------- Comparaisons ----------

    [Fact] public void Not_equal() => AssertRoundTrip(c => c.Id != 2);
    [Fact] public void Greater_than() => AssertRoundTrip(c => c.Id > 1);
    [Fact] public void Constant_on_the_left_is_mirrored() => AssertRoundTrip(c => 2 < c.Id);
    [Fact] public void Range_on_the_same_property() => AssertRoundTrip(c => c.Id >= 2 && c.Id <= 5);
    [Fact] public void Nullable_comparison() => AssertRoundTrip(c => c.SponsorId >= 1);
    [Fact] public void Enum_comparison() => AssertRoundTrip(c => c.Status > Status.Inactive);
    [Fact] public void Date_comparison() => AssertRoundTrip(c => c.Creation < new DateTime(2026, 7, 1));

    // ---------- Lists and text ----------

    [Fact] public void List_contains() { var ids = new List<long> { 1, 5 }; AssertRoundTrip(c => ids.Contains(c.Id)); }
    [Fact] public void Array_contains() { long[] ids = [2, 5]; AssertRoundTrip(c => ids.Contains(c.Id)); }
    [Fact] public void Enumerable_contains() { IEnumerable<string> names = ["Bob"]; AssertRoundTrip(c => names.Contains(c.Name)); }
    [Fact] public void Empty_list_selects_nothing() { var ids = new List<long>(); AssertRoundTrip(c => ids.Contains(c.Id)); }
    [Fact] public void Starts_with() => AssertRoundTrip(c => c.Name.StartsWith("An"));
    [Fact] public void Ends_with() => AssertRoundTrip(c => c.Name.EndsWith("belle"));
    [Fact] public void Text_contains() => AssertRoundTrip(c => c.Name.Contains("o"));

    // ---------- Client-side rejection ----------

    [Fact]
    public void Mixed_and_or_is_rejected()
        => Assert.Throws<NotSupportedException>(() => Filters.FromPredicate<Row>(c => c.Id == 1 && (c.Name == "a" || c.Name == "b")));

    [Fact]
    public void Comparing_two_properties_is_rejected()
        => Assert.Throws<NotSupportedException>(() => Filters.FromPredicate<Row>(c => c.Id > c.SponsorId));

    [Fact]
    public void Other_method_calls_are_rejected()
        => Assert.Throws<NotSupportedException>(() => Filters.FromPredicate<Row>(c => c.Name.ToUpper() == "ANA"));

    [Fact]
    public void Nested_member_is_rejected()
        => Assert.Throws<NotSupportedException>(() => Filters.FromPredicate<Row>(c => c.Name.Length > 3));

    [Fact]
    public void Text_value_containing_a_property_is_not_a_list()
        => Assert.Throws<NotSupportedException>(() => Filters.FromPredicate<Row>(c => "Ana Bob".Contains(c.Name)));

    // ---------- Server-side rejection (forged request) ----------

    private static void AssertServerRejects(params FilterCondition[] conditions)
        => Assert.Throws<ArgumentException>(() => Filters.ToPredicate<Row>(conditions, false));

    [Fact] public void Null_for_a_non_nullable_property_is_rejected() => AssertServerRejects(new FilterCondition("Id", FilterOperator.Equal, null));
    [Fact] public void Unknown_property_is_rejected() => AssertServerRejects(new FilterCondition("Inconnue", FilterOperator.Equal, 1));
    [Fact] public void Text_ordering_is_rejected() => AssertServerRejects(new FilterCondition("Name", FilterOperator.GreaterThan, "A"));
    [Fact] public void StartsWith_on_a_number_is_rejected() => AssertServerRejects(new FilterCondition("Id", FilterOperator.StartsWith, "1"));
    [Fact] public void In_without_a_list_is_rejected() => AssertServerRejects(new FilterCondition("Id", FilterOperator.In, 1));
    [Fact] public void List_with_equal_is_rejected() => AssertServerRejects(new FilterCondition("Id", FilterOperator.Equal, new object[] { 1, 2 }));
    [Fact] public void Wrong_value_type_is_rejected() => AssertServerRejects(new FilterCondition("Id", FilterOperator.Equal, "not a number"));
}

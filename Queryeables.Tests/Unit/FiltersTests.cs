using Queryeables.Abstraction;
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
        public bool? Verified { get; set; }
    }

    private static readonly Row[] Rows =
    [
        new() { Id = 1, Name = "Ana", Status = Status.Active, Active = true, SponsorId = null, Ref = Guid.Parse("11111111-1111-1111-1111-111111111111"), Creation = new DateTime(2026, 1, 1), Verified = true },
        new() { Id = 2, Name = "Bob", Status = Status.Inactive, Active = false, SponsorId = 1, Ref = Guid.Empty, Creation = new DateTime(2026, 6, 1), Verified = false },
        new() { Id = 5, Name = "Annabelle", Status = Status.Active, Active = true, SponsorId = 1, Ref = Guid.Empty, Creation = new DateTime(2026, 9, 1) },
    ];

    /// <summary>Simulates MessagePack: small numbers come back as bytes, including in lists.</summary>
    private static FilterCondition[] OverTheWire(FilterCondition[] conditions)
    {
        static object? Shrink(object? v) => v is long l && l is >= 0 and <= 255 ? (byte)l : v;
        return conditions.Select(c => new FilterCondition(c.Property, c.Operator,
            c.Value is object?[] list ? list.Select(Shrink).ToArray() : Shrink(c.Value))).ToArray();
    }

    /// <summary>What the server binds the names of a filter to: the queryable properties of Row.</summary>
    private static readonly IFilterScope Scope = new EntityHandler<Row>(new EntityStoreFactory(() => null!), EntityAccessPolicies.AllowAll).FilterScope(null);

    /// <summary>The translated, carried then rebuilt predicate must select the same rows.</summary>
    private static void AssertRoundTrip(Expression<Func<Row, bool>> predicate)
    {
        var (conditions, any) = Filters.FromPredicate(predicate);
        var rebuilt = Filters.ToPredicate<Row>(OverTheWire(conditions), any, RemoteMembers<Row>.Queryable, Scope).Compile();

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

    // ---------- AND, OR, parentheses and NOT ----------

    [Fact] public void And_of_an_or() => AssertRoundTrip(c => c.Active && (c.Name == "Bob" || c.Id == 5));
    [Fact] public void Or_of_ands() => AssertRoundTrip(c => (c.Id == 1 && c.Active) || (c.Id == 2 && !c.Active));
    [Fact] public void Deep_nesting() => AssertRoundTrip(c => c.Id > 0 && (c.Name == "Ana" || (c.Status == Status.Active && (c.Id == 5 || c.Id == 9))));
    [Fact] public void Negated_and_uses_de_morgan() => AssertRoundTrip(c => !(c.Id == 1 && c.Active));
    [Fact] public void Negated_or_uses_de_morgan() => AssertRoundTrip(c => !(c.Name == "Ana" || c.Name == "Bob"));
    [Fact] public void Negated_comparison() => AssertRoundTrip(c => !(c.Id > 1) || c.Name == "Bob");
    [Fact] public void Double_negation() => AssertRoundTrip(c => !!(c.Id == 2) && !(!c.Active || c.Id == 1));
    [Fact] public void Mixed_filter_with_a_list() { long[] ids = [1, 5]; AssertRoundTrip(c => ids.Contains(c.Id) && (c.Active || c.SponsorId == 1)); }

    [Fact]
    public void A_flat_filter_keeps_the_flat_form()
    {
        var (conditions, any) = Filters.FromPredicate<Row>(c => c.Id == 1 || c.Id == 2);
        Assert.Equal(2, conditions.Length);
        Assert.True(any);
    }

    [Fact]
    public void A_mixed_filter_is_sent_in_postfix_order()
    {
        var (conditions, _) = Filters.FromPredicate<Row>(c => c.Active && (c.Id == 1 || c.Id == 2));
        Assert.Equal(new[] { FilterOperator.Equal, FilterOperator.Equal, FilterOperator.Equal, FilterOperator.Or, FilterOperator.And },
            conditions.Select(c => c.Operator));
    }

    [Fact]
    public void A_forged_postfix_filter_is_rejected()
    {
        var term = new FilterCondition("Id", FilterOperator.Equal, 1);
        AssertServerRejects(term, new FilterCondition("", FilterOperator.And, 2));                        // one operand only
        AssertServerRejects(term, term, term, new FilterCondition("", FilterOperator.Or, 2));             // two left over
        AssertServerRejects(term, term, new FilterCondition("", FilterOperator.Or, "two"));               // not a number

        var deep = new List<FilterCondition> { term };
        for (var i = 0; i < 40; i++)
            deep.AddRange([term, new FilterCondition("", FilterOperator.And, 2)]);
        AssertServerRejects([.. deep]);                                                                    // too deep
    }

    // ---------- Client-side rejection ----------

    // ---------- The extended form: what the simple form could not say, with the rows C# gives ----------

    [Fact]
    public void Negating_a_list_or_a_text_search_keeps_the_rows_of_CSharp()
    {
        long[] ids = [1, 2];
        AssertRoundTrip(c => !ids.Contains(c.Id));
        AssertRoundTrip(c => !c.Name.StartsWith("A"));
    }

    [Fact]
    public void Negating_an_ordering_on_a_nullable_keeps_the_nulls_as_CSharp_does()
        => AssertRoundTrip(c => !(c.SponsorId > 0));

    [Fact]
    public void Negating_a_nullable_flag_keeps_the_nulls_as_CSharp_does()
    {
        AssertRoundTrip(c => !(c.Verified == true));
        AssertRoundTrip(c => c.Verified != true);
        AssertRoundTrip(c => !c.Active);
    }

    [Fact]
    public void Two_properties_are_compared() => AssertRoundTrip(c => c.Id > c.SponsorId);

    [Fact]
    public void Text_functions_compute_on_the_server()
    {
        AssertRoundTrip(c => c.Name.ToUpper() == "ANA");
        AssertRoundTrip(c => c.Name.Length > 2 && c.Name.ToLower().StartsWith("b"));
        AssertRoundTrip(c => c.Name.Substring(1) == "na" || c.Name + "!" == "Bob!");
        AssertRoundTrip(c => string.Compare(c.Name, "B") >= 0);
    }

    [Fact]
    public void Dates_and_arithmetic_compute_on_the_server()
    {
        AssertRoundTrip(c => c.Creation.Year == 2026 && c.Creation.Month > 3);
        AssertRoundTrip(c => c.Id * 2 + (c.SponsorId ?? 0) > 3);
        AssertRoundTrip(c => c.Id % 2 == 0);
    }

    [Fact]
    public void A_text_value_containing_a_property_is_a_text_search() => AssertRoundTrip(c => "Ana Bob".Contains(c.Name));

    [Fact]
    public void Other_method_calls_are_rejected()
        => Assert.Throws<NotSupportedException>(() => Filters.FromPredicate<Row>(c => c.Name.GetHashCode() == 1));

    [Fact]
    public void A_function_the_server_does_not_know_is_rejected()
        => Assert.Throws<ArgumentException>(() => Filters.ToPredicate<Row>(
            [new FilterCondition("Name", FilterOperator.Member, null), new FilterCondition("reverse", FilterOperator.Function, 1),
             new FilterCondition("", FilterOperator.Value, "x"), new FilterCondition("", FilterOperator.Equal, null)],
            false, RemoteMembers<Row>.Queryable, Scope));

    [Fact]
    public void A_navigation_toward_a_type_the_server_does_not_expose_is_unknown()
        => Assert.Throws<ArgumentException>(() => Filters.ToPredicate<Row>(
            [new FilterCondition("Owner.Name", FilterOperator.Member, null), new FilterCondition("", FilterOperator.Value, "x"),
             new FilterCondition("", FilterOperator.Equal, null)],
            false, RemoteMembers<Row>.Queryable, Scope));

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

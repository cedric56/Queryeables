using Queryeables.Abstraction;

namespace Queryeables.Tests.Unit;

/// <summary><c>Update</c> on a list: one item per source, created when missing, removed when gone.</summary>
public class CollectionUpdateTests
{
    [Fact]
    public void Update_pairs_items_one_to_one()
    {
        var contacts = new List<MemberContact> { new() { PersonId = 1, Label = "kept" }, new() { PersonId = 3, Label = "removed" } };

        contacts.Update(new long[] { 1, 2 }, (id, c) => c.PersonId == id, id => new MemberContact { PersonId = id, Label = "new" });

        Assert.Equal(new[] { "kept", "new" }, contacts.Select(c => c.Label));
    }

    [Fact]
    public void Update_works_with_value_types_and_records()
    {
        var numbers = new List<int> { 0, 5 };
        numbers.Update(new[] { 0, 7 }, (s, t) => s == t, s => s);
        Assert.Equal(new[] { 0, 7 }, numbers);

        // Two equal records stay two items.
        var tags = new List<NameTag> { new("a"), new("a") };
        tags.Update(new[] { "a", "a" }, (s, t) => t.Name == s, s => new NameTag(s));
        Assert.Equal(2, tags.Count);
    }

    [Fact]
    public void Update_keeps_a_created_item_that_does_not_match_its_source()
    {
        var target = new List<string>();
        target.Update(new[] { 1 }, (s, t) => t == s.ToString(), s => $"#{s}");
        Assert.Equal(new[] { "#1" }, target);
    }
}

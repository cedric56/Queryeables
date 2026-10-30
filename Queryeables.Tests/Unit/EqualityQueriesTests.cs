using Queryeables.Abstraction;
using Queryeables.Dommel;
using System.Linq.Expressions;

namespace Queryeables.Tests.Unit;

/// <summary>Which predicates take the Dommel fast path (one column equal to a value).</summary>
public class EqualityQueriesTests
{
    private sealed class Child
    {
        public long Id { get; set; }
        public long? ParentId { get; set; }
        public string Name { get; set; } = "";
    }

    private static bool Matches(Expression<Func<Child, bool>> predicate, out string property, out object? value)
    {
        var matched = EqualityQueries.TryMatch(CapturedValues.Inline(predicate), out var info, out var raw);
        property = info?.Name ?? "";
        value = raw;
        return matched;
    }

    [Fact]
    public void A_key_compared_with_a_captured_value()
    {
        long id = 42;
        Assert.True(Matches(c => c.Id == id, out var property, out var value));
        Assert.Equal("Id", property);
        Assert.Equal((object)42L, value);
    }

    [Fact]
    public void A_captured_value_without_inlining()
    {
        long id = 5;
        Assert.True(EqualityQueries.TryMatch<Child>(c => c.Id == id, out var property, out var value));
        Assert.Equal("Id", property.Name);
        Assert.Equal((object)5L, value);
    }

    [Fact]
    public void A_value_on_the_left()
    {
        Assert.True(Matches(c => "Ana" == c.Name, out var property, out var value));
        Assert.Equal("Name", property);
        Assert.Equal((object)"Ana", value);
    }

    /// <summary>The form EntityGraph builds for a nullable foreign key: Convert(c.ParentId) == constant.</summary>
    [Fact]
    public void A_converted_foreign_key()
    {
        var parameter = Expression.Parameter(typeof(Child), "c");
        var body = Expression.Equal(
            Expression.Convert(Expression.Property(parameter, nameof(Child.ParentId)), typeof(long)),
            Expression.Constant(7L, typeof(long)));
        Assert.True(EqualityQueries.TryMatch(Expression.Lambda<Func<Child, bool>>(body, parameter), out var property, out var value));
        Assert.Equal("ParentId", property.Name);
        Assert.Equal((object)7L, value);
    }

    [Fact]
    public void Other_predicates_go_through_Dommel()
    {
        Assert.False(Matches(c => c.ParentId == null, out _, out _));                 // null: "is null" in SQL
        Assert.False(Matches(c => c.Id == 1 && c.Name == "Ana", out _, out _));       // two conditions
        Assert.False(Matches(c => c.Id > 1, out _, out _));                           // not an equality
        Assert.False(Matches(c => c.Name.Length == 3, out _, out _));                 // not a column
        Assert.False(Matches(c => c.Id == c.ParentId, out _, out _));                 // two columns
    }
}

using Queryeables.Abstraction;
using System.Linq.Expressions;

namespace Queryeables.Tests.Unit;

public class CapturedValuesTests
{
    private sealed class Holder
    {
        public long Id = 2;
        public string Name { get; set; } = "Bob";
    }

    private static readonly long StaticId = 1;

    private static readonly TestClient[] Rows = [new() { Id = 1, Name = "Ana" }, new() { Id = 2, Name = "Bob" }, new() { Id = 3, Name = "Cy" }];

    private static bool HasCapturedMember(Expression expression)
    {
        var finder = new CapturedFinder();
        finder.Visit(expression);
        return finder.Found;
    }

    private sealed class CapturedFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is not ParameterExpression)
                Found = true;
            return base.VisitMember(node);
        }
    }

    private static void AssertSameRows(Expression<Func<TestClient, bool>> predicate, bool keepsCapturedMembers = false)
    {
        var inlined = CapturedValues.Inline(predicate);
        Assert.Equal(keepsCapturedMembers, HasCapturedMember(inlined.Body));
        Assert.Equal(Rows.Where(predicate.Compile()).Select(r => r.Id), Rows.Where(inlined.Compile()).Select(r => r.Id));
    }

    [Fact] public void A_captured_local() { long id = 2; AssertSameRows(c => c.Id == id); }

    [Fact] public void A_field_of_a_captured_object() { var holder = new Holder(); AssertSameRows(c => c.Id == holder.Id || c.Name == holder.Name); }

    [Fact] public void A_static_field() => AssertSameRows(c => c.Id > StaticId);

    /// <summary>Dommel only accepts a variable in <c>ids.Contains(e.Id)</c>, not a constant list.</summary>
    [Fact] public void A_captured_list_is_kept() { var ids = new List<long> { 1, 3 }; AssertSameRows(c => ids.Contains(c.Id), keepsCapturedMembers: true); }

    [Fact] public void A_captured_text_is_inlined() { var start = "A"; AssertSameRows(c => c.Name.StartsWith(start)); }

    [Fact]
    public void Members_of_the_parameter_and_method_calls_are_kept()
    {
        Assert.False(CapturedValues.TryEvaluate(((Expression<Func<long>>)(() => Math.Abs(-1L))).Body, out _));

        Expression<Func<TestClient, bool>> predicate = c => c.Name.Length > 2;
        Assert.Equal(predicate.Body.ToString(), CapturedValues.Inline(predicate).Body.ToString());
    }

    [Fact]
    public void The_value_is_read_when_inlining()
    {
        long id = 1;
        Expression<Func<TestClient, bool>> predicate = c => c.Id == id;
        var first = CapturedValues.Inline(predicate);
        id = 3;
        var second = CapturedValues.Inline(predicate);

        Assert.Equal(1L, Rows.Single(first.Compile()).Id);
        Assert.Equal(3L, Rows.Single(second.Compile()).Id);
    }
}

using Queryeables.Abstraction;
using Queryeables.Remote;

namespace Queryeables.Tests.Unit;

public class SortAndProjectionTests
{
    private readonly MemoryDatabase _database = new();

    private async Task SeedAsync()
    {
        await using var store = new MemoryStore(_database);
        foreach (var (label, owner) in new[] { ("b", 2L), ("a", 1L), ("c", 2L), ("d", 3L) })
            await store.InsertAsync(new MemberContact { Label = label, PersonId = owner });
    }

    [Fact]
    public async Task A_local_store_sorts_and_pages()
    {
        await SeedAsync();
        await using var store = new MemoryStore(_database);

        var page = await store.SelectPagedAsync<MemberContact>(c => c.Id > 0, 1, 3,
            Sort<MemberContact>.ByDescending(c => c.PersonId).ThenBy(c => c.Label));

        Assert.Equal(new[] { "d", "b", "c" }, page.Select(c => c.Label));
    }

    [Fact]
    public async Task A_sorted_select_without_paging()
    {
        await SeedAsync();
        await using var store = new MemoryStore(_database);

        var rows = await store.SelectAsync<MemberContact>(c => c.Id > 0, Sort<MemberContact>.By(c => c.Label));

        Assert.Equal(new[] { "a", "b", "c", "d" }, rows.Select(c => c.Label));
    }

    [Fact]
    public void A_forged_sort_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Sort<MemberContact>.FromKeys([new SortKey("Password")]));
        Assert.Throws<ArgumentException>(() => Sort<MemberContact>.FromKeys([new SortKey("Label"), new SortKey("Label", true)]));
        Assert.Throws<ArgumentException>(() => Sort<MemberContact>.FromKeys([]));
        Assert.Throws<ArgumentException>(() => Sort<MemberContact>.FromKeys(Enumerable.Range(0, 9).Select(_ => new SortKey("Id"))));
        Assert.Throws<ArgumentException>(() => Sort<MemberContact>.By(c => c.Label.Length));
    }

}

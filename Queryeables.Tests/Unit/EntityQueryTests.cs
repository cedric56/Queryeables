using Queryeables.Abstraction;
using Queryeables.Remote;

namespace Queryeables.Tests.Unit;

public class EntityQueryTests
{
    private readonly MemoryDatabase _database = new();

    public EntityQueryTests()
    {
        using var store = new MemoryStore(_database);
        foreach (var (label, owner) in new[] { ("a", 1L), ("b", 2L), ("c", 2L), ("d", 3L), ("e", 3L), ("f", 3L), ("g", 4L) })
            store.InsertAsync(new AuditContact { Label = label, PersonId = owner }).GetAwaiter().GetResult();
    }

    private IEntityStoreFactory Local() => new EntityStoreFactory(() => new MemoryStore(_database));

    private IEntityStoreFactory Remote()
    {
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<AuditContact>(Local(), EntityAccessPolicies.AllowAll));
        var dispatcher = new EntityRequestDispatcher(registry, new JsonTestSerializer(), Local());
        return RemoteEntityStore.Factory(dispatcher.AsRemote(), new JsonTestSerializer());
    }

    private static IEnumerable<AuditContact> All()
        => new[] { ("a", 1L), ("b", 2L), ("c", 2L), ("d", 3L), ("e", 3L), ("f", 3L), ("g", 4L) }
            .Select((x, i) => new AuditContact { Id = i + 1, Label = x.Item1, PersonId = x.Item2 });

    /// <summary>The same query through LINQ to Objects: the expected result.</summary>
    private async Task AssertLikeLinqAsync(Func<EntityQuery<AuditContact>, EntityQuery<AuditContact>> query,
        Func<IEnumerable<AuditContact>, IEnumerable<AuditContact>> linq)
    {
        var expected = linq(All()).Select(c => c.Label).ToList();
        foreach (var stores in new[] { Local(), Remote() })
            Assert.Equal(expected, (await query(stores.Query<AuditContact>()).ToListAsync()).Select(c => c.Label));
    }

    [Fact]
    public Task Several_wheres_are_joined_with_and()
        => AssertLikeLinqAsync(q => q.Where(c => c.PersonId >= 2).Where(c => c.Label != "e"),
            l => l.Where(c => c.PersonId >= 2).Where(c => c.Label != "e"));

    [Fact]
    public Task Sorted_on_several_keys()
        => AssertLikeLinqAsync(q => q.OrderByDescending(c => c.PersonId).ThenBy(c => c.Label),
            l => l.OrderByDescending(c => c.PersonId).ThenBy(c => c.Label));

    [Fact]
    public Task Skip_and_take_aligned_on_pages()
        => AssertLikeLinqAsync(q => q.OrderBy(c => c.Label).Skip(2).Take(2), l => l.OrderBy(c => c.Label).Skip(2).Take(2));

    [Fact]
    public Task Skip_not_a_multiple_of_take()
        => AssertLikeLinqAsync(q => q.OrderByDescending(c => c.Label).Skip(3).Take(2), l => l.OrderByDescending(c => c.Label).Skip(3).Take(2));

    [Fact]
    public Task Skip_past_the_end_and_a_short_last_page()
        => AssertLikeLinqAsync(q => q.Where(c => c.PersonId == 3).Skip(1).Take(5), l => l.Where(c => c.PersonId == 3).Skip(1).Take(5));

    [Fact]
    public Task Skip_without_take_and_take_then_skip()
        => AssertLikeLinqAsync(q => q.OrderBy(c => c.Id).Take(5).Skip(2).Skip(1), l => l.OrderBy(c => c.Id).Take(5).Skip(2).Skip(1));

    [Fact]
    public Task Two_takes_keep_the_smaller()
        => AssertLikeLinqAsync(q => q.OrderBy(c => c.Id).Take(4).Take(2), l => l.OrderBy(c => c.Id).Take(4).Take(2));

    [Fact]
    public Task Page_is_skip_and_take()
        => AssertLikeLinqAsync(q => q.OrderBy(c => c.Label).Page(3, 3), l => l.OrderBy(c => c.Label).Skip(6).Take(3));

    [Fact]
    public async Task Projection_first_count_and_any()
    {
        foreach (var stores in new[] { Local(), Remote() })
        {
            var query = stores.Query<AuditContact>().Where(c => c.PersonId == 3).OrderByDescending(c => c.Label);

            Assert.Equal(new[] { "F", "E" }, await query.Take(2).Select(c => c.Label.ToUpperInvariant()).ToListAsync());
            Assert.Equal(new[] { "d", "e", "f" }, (await query.Select(c => c.Label).ToListAsync()).OrderBy(l => l));
            Assert.Equal("f", (await query.FirstOrDefaultAsync())?.Label);
            Assert.Equal("e", await query.Skip(1).Select(c => c.Label).FirstOrDefaultAsync());
            Assert.Equal(3L, await query.CountAsync());
            Assert.Equal(1L, await query.Skip(2).Take(5).CountAsync());
            Assert.True(await query.AnyAsync());
            Assert.False(await query.Where(c => c.Label == "z").AnyAsync());
            await Assert.ThrowsAsync<EntityStoreException>(() => query.Where(c => c.Label == "z").FirstAsync());
        }
    }

    [Fact]
    public async Task A_query_on_an_open_store_and_shared_base_queries()
    {
        await using var store = new MemoryStore(_database);
        var owners2 = store.Query<AuditContact>().Where(c => c.PersonId == 2);

        var both = await owners2.ToListAsync();
        var onlyC = await owners2.Where(c => c.Label == "c").ToListAsync();

        Assert.Equal(2, both.Count);
        Assert.Equal("c", Assert.Single(onlyC).Label);
        Assert.Throws<InvalidOperationException>(() => owners2.ThenBy(c => c.Label));
        Assert.Empty(await owners2.Take(0).ToListAsync());
    }
}

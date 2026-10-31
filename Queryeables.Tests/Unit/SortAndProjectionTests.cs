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
            await store.InsertAsync(new AuditContact { Label = label, PersonId = owner });
    }

    private IEntityStoreFactory Remote()
    {
        var stores = new EntityStoreFactory(() => new MemoryStore(_database));
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<AuditContact>(stores, EntityAccessPolicies.AllowAll));
        var dispatcher = new EntityRequestDispatcher(registry, new JsonTestSerializer(), stores);
        return RemoteEntityStore.Factory(dispatcher.AsTransport(), new JsonTestSerializer());
    }

    [Fact]
    public async Task A_local_store_sorts_and_pages()
    {
        await SeedAsync();
        await using var store = new MemoryStore(_database);

        var page = await store.SelectPagedAsync<AuditContact>(c => c.Id > 0, 1, 3,
            Sort<AuditContact>.ByDescending(c => c.PersonId).ThenBy(c => c.Label));

        Assert.Equal(new[] { "d", "b", "c" }, page.Select(c => c.Label));
    }

    [Fact]
    public async Task A_remote_store_is_sorted_by_the_server()
    {
        await SeedAsync();
        await using var store = Remote().Create();

        var first = await store.SelectPagedAsync<AuditContact>(c => c.PersonId == 2 || c.PersonId == 1, 1, 2,
            Sort<AuditContact>.ByDescending(c => c.Label));
        var second = await store.SelectPagedAsync<AuditContact>(c => c.PersonId == 2 || c.PersonId == 1, 2, 2,
            Sort<AuditContact>.ByDescending(c => c.Label));

        Assert.Equal(new[] { "c", "b" }, first.Select(c => c.Label));
        Assert.Equal(new[] { "a" }, second.Select(c => c.Label));
    }

    [Fact]
    public async Task A_projection_returns_only_what_is_selected()
    {
        await SeedAsync();
        await using var store = Remote().Create();

        var labels = await store.SelectAsync<AuditContact, string>(c => c.PersonId == 2, c => c.Label.ToUpperInvariant());

        Assert.Equal(new[] { "B", "C" }, labels.OrderBy(l => l));
    }

    [Fact]
    public async Task A_sorted_select_without_paging()
    {
        await SeedAsync();
        await using var store = new MemoryStore(_database);

        var rows = await store.SelectAsync<AuditContact>(c => c.Id > 0, Sort<AuditContact>.By(c => c.Label));

        Assert.Equal(new[] { "a", "b", "c", "d" }, rows.Select(c => c.Label));
    }

    [Fact]
    public void A_forged_sort_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Sort<AuditContact>.FromKeys([new SortKey("Password")]));
        Assert.Throws<ArgumentException>(() => Sort<AuditContact>.FromKeys([new SortKey("Label"), new SortKey("Label", true)]));
        Assert.Throws<ArgumentException>(() => Sort<AuditContact>.FromKeys([]));
        Assert.Throws<ArgumentException>(() => Sort<AuditContact>.FromKeys(Enumerable.Range(0, 9).Select(_ => new SortKey("Id"))));
        Assert.Throws<ArgumentException>(() => Sort<AuditContact>.By(c => c.Label.Length));
    }

    [Fact]
    public async Task The_server_reports_an_unknown_sort_property()
    {
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<AuditContact>(new EntityStoreFactory(() => new MemoryStore(_database)), EntityAccessPolicies.AllowAll));
        var dispatcher = new EntityRequestDispatcher(registry, new JsonTestSerializer());

        var result = await dispatcher.SelectPagedAsync(EntityName.Of<AuditContact>(), [], false, 1, 10, [new SortKey("Nope")], EntityRequestContext.Anonymous);

        Assert.False(result.Successful);
        Assert.Equal(EntityErrorCode.InvalidRequest, result.ErrorCode);
    }
}

using Queryeables.Abstraction;
using Queryeables.Remote;
using System.Security.Claims;

namespace Queryeables.Tests.Unit;

/// <summary>What a remote client may reach: the rows of its user only, and never a whole table at once.</summary>
public class ServerLimitsTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();

    public ServerLimitsTests()
    {
        var store = Server().Create();
        foreach (var (name, sponsor) in new[] { ("Ana", 1), ("Bob", 2), ("Carl", 1), ("Dana", 2), ("Eve", 2) })
            store.InsertAsync(new TestClient { Name = name, SponsorId = sponsor }).GetAwaiter().GetResult();
    }

    private IEntityStoreFactory Server() => new EntityStoreFactory(() => new MemoryStore(_database).WithSyncConventions());

    /// <summary>A client signed in as a user of <paramref name="sponsor"/>; the server keeps each user to its sponsor's rows.</summary>
    private IEntityStore Client(int sponsor, int maxReadSize = EntityHandler.DefaultMaxReadSize)
    {
        var rows = EntityRowFilter.For<TestClient, long>(c => c.Id, user =>
        {
            var mine = int.Parse(user?.FindFirst("sponsor")?.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            return c => c.SponsorId == mine;
        });
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<TestClient>(Server(), EntityAccessPolicies.Authenticated, rows: rows, maxReadSize: maxReadSize));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sponsor", sponsor.ToString(System.Globalization.CultureInfo.InvariantCulture))], "test"));
        var remote = new EntityRequestDispatcher(registry, _serializer, Server()).AsRemote(new EntityRequestContext(user));
        return new RemoteEntityStore(remote, _serializer);
    }

    private IEntityStore Unbounded(int maxReadSize)
    {
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<TestClient>(Server(), EntityAccessPolicies.AllowAll, maxReadSize: maxReadSize));
        return new RemoteEntityStore(new EntityRequestDispatcher(registry, _serializer, Server()).AsRemote(), _serializer);
    }

    [Fact]
    public async Task Every_read_returns_the_rows_of_the_user_only()
    {
        await using var store = Client(sponsor: 1);

        Assert.Equal(["Ana", "Carl"], (await store.GetAllAsync<TestClient>()).Select(c => c.Name).Order());
        Assert.Equal(["Carl"], (await store.SelectAsync<TestClient>(c => c.Name != "Ana")).Select(c => c.Name));
        Assert.Equal(["Ana", "Carl"], (await store.SelectPagedAsync<TestClient>(c => true, 1, 10, Sort<TestClient>.By(c => c.Name)))
            .Select(c => c.Name));
        Assert.Null(await store.FirstOrDefaultAsync<TestClient>(c => c.Name == "Bob"));
        Assert.Equal(["Ana", "Carl"], (await store.SelectChangesAsync<TestClient>(DateTime.UtcNow.AddHours(-1))).Select(c => c.Name).Order());
    }

    [Fact]
    public async Task The_row_of_another_user_cannot_be_changed_nor_deleted()
    {
        var bob = _database.All<TestClient>().Single(c => c.Name == "Bob").Copy();
        await using var store = Client(sponsor: 1);

        bob.Name = "Hacked";
        var update = await Assert.ThrowsAsync<EntityStoreException>(() => store.UpdateAsync(bob));
        var delete = await Assert.ThrowsAsync<EntityStoreException>(() => store.DeleteAsync(bob));

        Assert.Equal(EntityErrorCode.NotFound, update.ErrorCode);       // as if it did not exist
        Assert.Equal(EntityErrorCode.NotFound, delete.ErrorCode);
        var stored = _database.All<TestClient>().Single(c => c.Id == bob.Id);
        Assert.Equal("Bob", stored.Name);
        Assert.False(stored.Deleted);
    }

    [Fact]
    public async Task A_row_cannot_be_written_outside_the_rows_of_the_user()
    {
        await using var store = Client(sponsor: 1);

        var insert = await Assert.ThrowsAsync<EntityStoreException>(() => store.InsertAsync(new TestClient { Name = "Intruder", SponsorId = 2 }));
        var ana = (await store.SelectAsync<TestClient>(c => c.Name == "Ana")).Single();
        ana.SponsorId = 2;      // given away
        var move = await Assert.ThrowsAsync<EntityStoreException>(() => store.UpdateAsync(ana));

        Assert.Equal(EntityErrorCode.Forbidden, insert.ErrorCode);
        Assert.Equal(EntityErrorCode.Forbidden, move.ErrorCode);
        Assert.Equal(5, _database.All<TestClient>().Count);
        Assert.Equal(1, _database.All<TestClient>().Single(c => c.Name == "Ana").SponsorId);
    }

    [Fact]
    public async Task The_rows_of_the_user_are_written_as_usual()
    {
        await using var store = Client(sponsor: 1);

        var carl = (await store.SelectAsync<TestClient>(c => c.Name == "Carl")).Single();
        carl.Name = "Carla";
        Assert.True(await store.UpdateAsync(carl));
        await store.InsertAsync(new TestClient { Name = "Fay", SponsorId = 1 });
        var result = await store.RunInTransactionAsync(async s =>
        {
            var ana = (await s.SelectAsync<TestClient>(c => c.Name == "Ana")).Single();
            await s.DeleteAsync(ana);
        });

        Assert.True(result.Successful);
        Assert.Equal(["Carla", "Fay"], (await store.GetAllAsync<TestClient>()).Select(c => c.Name).Order());
    }

    [Fact]
    public async Task A_batch_touching_another_users_row_applies_nothing()
    {
        var bob = _database.All<TestClient>().Single(c => c.Name == "Bob").Copy();
        await using var store = Client(sponsor: 1);

        var result = await store.RunInTransactionAsync(async s =>
        {
            await s.InsertAsync(new TestClient { Name = "Fay", SponsorId = 1 });
            await s.DeleteAsync(bob);
        });

        Assert.False(result.Successful);
        Assert.Equal(EntityErrorCode.NotFound, result.ErrorCode);
        Assert.Equal(5, _database.All<TestClient>().Count);
    }

    [Fact]
    public async Task A_read_that_is_not_paged_cannot_return_more_than_the_limit()
    {
        await using var store = Unbounded(maxReadSize: 3);

        var all = await Assert.ThrowsAsync<EntityStoreException>(() => store.GetAllAsync<TestClient>());
        var filtered = await Assert.ThrowsAsync<EntityStoreException>(() => store.SelectAsync<TestClient>(c => c.Name != "Ana"));
        var changes = await Assert.ThrowsAsync<EntityStoreException>(() => store.SelectChangesAsync<TestClient>(DateTime.MinValue));

        Assert.Equal(EntityErrorCode.InvalidRequest, all.ErrorCode);
        Assert.Equal(EntityErrorCode.InvalidRequest, filtered.ErrorCode);
        Assert.Equal(EntityErrorCode.InvalidRequest, changes.ErrorCode);
        Assert.Equal(3, (await store.SelectAsync<TestClient>(c => c.SponsorId == 2)).Count);     // at the limit: fine
        Assert.Single(await store.SelectPagedAsync<TestClient>(c => true, 3, 2));                // pages: as usual
    }

    [Fact]
    public async Task The_limit_counts_the_rows_of_the_user_only()
    {
        await using var store = Client(sponsor: 1, maxReadSize: 2);

        Assert.Equal(2, (await store.GetAllAsync<TestClient>()).Count);
    }
}

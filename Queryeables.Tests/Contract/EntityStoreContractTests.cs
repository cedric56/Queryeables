using Queryeables.Abstraction;

namespace Queryeables.Tests.Contract;

/// <summary>
/// Contract shared by every <see cref="IEntityStore"/> implementation.
/// For a new implementation: derive from this class and provide <see cref="CreateStoreAsync"/>.
/// Each test starts from an empty database containing the Clients table.
/// </summary>
public abstract class EntityStoreContractTests : IAsyncLifetime
{
    /// <summary>Prepares an empty database (Clients table created).</summary>
    public abstract Task InitializeAsync();

    public abstract Task DisposeAsync();

    /// <summary>Creates a store on the test database. The test disposes it.</summary>
    protected abstract IEntityStore CreateStore();

    /// <summary>False for a store that does not support transactions.</summary>
    protected virtual bool SupportsTransactions => true;

    /// <summary>True when the store applies [LastModified] and [SoftDelete] (<see cref="SyncConventionsDecorator"/>).</summary>
    protected virtual bool UsesSyncConventions => false;

    private async Task<TestClient> InsertAsync(string name, bool active = true, int? sponsorId = null)
    {
        var client = new TestClient { Name = name, Active = active, SponsorId = sponsorId };
        await using var store = CreateStore();
        await store.InsertAsync(client);
        return client;
    }

    [Fact]
    public async Task Insert_assigns_the_generated_key()
    {
        var first = await InsertAsync("Ana");
        var second = await InsertAsync("Bob");

        Assert.True(first.Id > 0);
        Assert.True(second.Id > first.Id);
    }

    [Fact]
    public async Task GetAll_returns_every_row()
    {
        await InsertAsync("Ana");
        await InsertAsync("Bob");

        await using var store = CreateStore();
        var all = await store.GetAllAsync<TestClient>();

        Assert.Equal(new[] { "Ana", "Bob" }, all.Select(c => c.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task Select_filters_with_equalities()
    {
        await InsertAsync("Ana", active: true, sponsorId: 1);
        await InsertAsync("Bob", active: false, sponsorId: 1);
        await InsertAsync("Cid", active: true, sponsorId: 2);

        await using var store = CreateStore();
        var result = await store.SelectAsync<TestClient>(c => c.Active == true && c.SponsorId == 1);

        Assert.Equal("Ana", Assert.Single(result).Name);
    }

    [Fact]
    public async Task Select_handles_null_values()
    {
        await InsertAsync("Ana", sponsorId: null);
        await InsertAsync("Bob", sponsorId: 3);

        await using var store = CreateStore();
        var result = await store.SelectAsync<TestClient>(c => c.SponsorId == null);

        Assert.Equal("Ana", Assert.Single(result).Name);
    }

    [Fact]
    public async Task FirstOrDefault_returns_null_when_nothing_matches()
    {
        await InsertAsync("Ana");

        await using var store = CreateStore();

        Assert.Null(await store.FirstOrDefaultAsync<TestClient>(c => c.Name == "Nobody"));
        Assert.Equal("Ana", (await store.FirstOrDefaultAsync<TestClient>(c => c.Name == "Ana"))?.Name);
    }

    [Fact]
    public async Task SelectPaged_returns_stable_pages_starting_at_one()
    {
        foreach (var name in new[] { "A", "B", "C", "D", "E" })
            await InsertAsync(name);

        await using var store = CreateStore();
        var page1 = await store.SelectPagedAsync<TestClient>(c => c.Active == true, 1, 2);
        var page2 = await store.SelectPagedAsync<TestClient>(c => c.Active == true, 2, 2);
        var page3 = await store.SelectPagedAsync<TestClient>(c => c.Active == true, 3, 2);

        var ids = page1.Concat(page2).Concat(page3).Select(c => c.Id).ToList();
        Assert.Equal(5, ids.Count);
        Assert.Equal(ids.OrderBy(id => id), ids); // sorted by key
        Assert.Single(page3);
    }

    [Fact]
    public async Task SelectPaged_sorts_by_the_given_keys()
    {
        await InsertAsync("B", sponsorId: 2);
        await InsertAsync("A", sponsorId: 1);
        await InsertAsync("C", sponsorId: 2);
        await InsertAsync("D", sponsorId: null);
        await InsertAsync("E", active: false);

        await using var store = CreateStore();
        var byName = await store.SelectPagedAsync<TestClient>(c => c.Active == true, 1, 10,
            Sort<TestClient>.ByDescending(c => c.Name));
        var bySponsorThenName = await store.SelectPagedAsync<TestClient>(c => c.SponsorId != null, 1, 10,
            Sort<TestClient>.ByDescending(c => c.SponsorId).ThenBy(c => c.Name));
        var secondPage = await store.SelectPagedAsync<TestClient>(c => c.Active == true, 2, 3, Sort<TestClient>.By(c => c.Name));

        Assert.Equal(new[] { "D", "C", "B", "A" }, byName.Select(c => c.Name));
        Assert.Equal(new[] { "B", "C", "A" }, bySponsorThenName.Select(c => c.Name));
        Assert.Equal(new[] { "D" }, secondPage.Select(c => c.Name));
    }

    [Fact]
    public async Task Query_composes_where_order_skip_take_and_select()
    {
        foreach (var name in new[] { "E", "B", "D", "A", "C" })
            await InsertAsync(name, sponsorId: name is "A" or "B" ? 1 : 2);
        await InsertAsync("F", active: false);

        await using var store = CreateStore();
        var active = store.Query<TestClient>().Where(c => c.Active == true);

        var page = await active.OrderBy(c => c.Name).Skip(1).Take(3).Select(c => c.Name).ToListAsync();
        var unaligned = await active.OrderByDescending(c => c.Name).Skip(1).Take(2).ToListAsync();
        var filtered = await active.Where(c => c.SponsorId == 2).OrderBy(c => c.Name).ToListAsync();
        var everything = await store.Query<TestClient>().OrderBy(c => c.Name).Take(2).ToListAsync();

        Assert.Equal(new[] { "B", "C", "D" }, page);
        Assert.Equal(new[] { "D", "C" }, unaligned.Select(c => c.Name));
        Assert.Equal(new[] { "C", "D", "E" }, filtered.Select(c => c.Name));
        Assert.Equal(new[] { "A", "B" }, everything.Select(c => c.Name));
        Assert.Equal(5L, await active.CountAsync());
        Assert.Equal("A", (await active.OrderBy(c => c.Name).FirstAsync()).Name);
    }

    [Fact]
    public async Task Select_projects_with_a_selector()
    {
        await InsertAsync("A", sponsorId: 1);
        await InsertAsync("B", active: false);

        await using var store = CreateStore();
        var names = await store.SelectAsync<TestClient, string>(c => c.Active == true, c => c.Name);
        var pairs = await store.SelectAsync<TestClient, (string Name, int? Sponsor)>(c => c.Active == true, c => ValueTuple.Create(c.Name, c.SponsorId));

        Assert.Equal(new[] { "A" }, names);
        Assert.Equal(("A", (int?)1), Assert.Single(pairs));
    }

    [Fact]
    public async Task SelectPaged_rejects_page_zero()
    {
        await using var store = CreateStore();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.SelectPagedAsync<TestClient>(c => c.Active == true, 0, 10));
    }

    [Fact]
    public async Task Update_returns_true_and_persists()
    {
        var client = await InsertAsync("Ana");
        client.Name = "Ana updated";

        await using (var store = CreateStore())
            Assert.True(await store.UpdateAsync(client));

        await using var check = CreateStore();
        Assert.Equal("Ana updated", (await check.FirstOrDefaultAsync<TestClient>(c => c.Id == client.Id))?.Name);
    }

    [Fact]
    public async Task Update_returns_false_for_a_missing_row()
    {
        await using var store = CreateStore();
        Assert.False(await store.UpdateAsync(new TestClient { Id = 999, Name = "Ghost" }));
    }

    [Fact]
    public async Task Delete_returns_true_then_false()
    {
        var client = await InsertAsync("Ana");

        await using var store = CreateStore();
        Assert.True(await store.DeleteAsync(client));
        Assert.False(await store.DeleteAsync(client));
        Assert.Empty(await store.GetAllAsync<TestClient>());
    }

    [Fact]
    public async Task Count_counts_matching_rows()
    {
        await InsertAsync("Ana", active: true);
        await InsertAsync("Bob", active: false);
        await InsertAsync("Cid", active: true);

        await using var store = CreateStore();
        Assert.Equal(2, await store.CountAsync<TestClient>(c => c.Active == true));
    }

    [Fact]
    public async Task Select_with_comparisons_and_ranges()
    {
        var a = await InsertAsync("A");
        var b = await InsertAsync("B");
        var c = await InsertAsync("C");

        await using var store = CreateStore();
        var after = await store.SelectAsync<TestClient>(x => x.Id > a.Id);
        var range = await store.SelectAsync<TestClient>(x => x.Id >= b.Id && x.Id <= c.Id);
        var notB = await store.SelectAsync<TestClient>(x => x.Id != b.Id);

        Assert.Equal(new[] { b.Id, c.Id }, after.Select(x => x.Id).OrderBy(id => id));
        Assert.Equal(new[] { b.Id, c.Id }, range.Select(x => x.Id).OrderBy(id => id));
        Assert.Equal(new[] { a.Id, c.Id }, notB.Select(x => x.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task Select_with_a_list_of_values()
    {
        var a = await InsertAsync("A");
        await InsertAsync("B");
        var c = await InsertAsync("C");
        var wanted = new List<long> { a.Id, c.Id };

        await using var store = CreateStore();
        var result = await store.SelectAsync<TestClient>(x => wanted.Contains(x.Id));

        Assert.Equal(new[] { "A", "C" }, result.Select(x => x.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task Select_with_text_searches()
    {
        await InsertAsync("Anna");
        await InsertAsync("Bob");
        await InsertAsync("Annabelle");

        await using var store = CreateStore();

        Assert.Equal(2, (await store.SelectAsync<TestClient>(x => x.Name.StartsWith("An"))).Count);
        Assert.Equal("Annabelle", Assert.Single(await store.SelectAsync<TestClient>(x => x.Name.EndsWith("belle"))).Name);
        Assert.Equal("Bob", Assert.Single(await store.SelectAsync<TestClient>(x => x.Name.Contains("o"))).Name);
    }

    [Fact]
    public async Task Update_increments_the_version()
    {
        var client = await InsertAsync("Ana");
        var before = client.Version;

        await using var store = CreateStore();
        Assert.True(await store.UpdateAsync(client));

        Assert.Equal(before + 1, client.Version);
        Assert.Equal(client.Version, (await store.FirstOrDefaultAsync<TestClient>(x => x.Id == client.Id))?.Version);
    }

    [Fact]
    public async Task Stale_update_is_a_conflict()
    {
        var client = await InsertAsync("Ana");
        var stale = client.Copy();

        await using var store = CreateStore();
        client.Name = "Changed by someone else";
        Assert.True(await store.UpdateAsync(client));

        stale.Name = "Overwrite";
        var error = await Assert.ThrowsAsync<EntityStoreException>(() => store.UpdateAsync(stale));
        Assert.Equal(EntityErrorCode.Conflict, error.ErrorCode);
        Assert.Equal("Changed by someone else", (await store.FirstOrDefaultAsync<TestClient>(x => x.Id == client.Id))?.Name);
    }

    [Fact]
    public async Task Stale_delete_is_a_conflict()
    {
        var client = await InsertAsync("Ana");
        var stale = client.Copy();

        await using var store = CreateStore();
        Assert.True(await store.UpdateAsync(client));

        var error = await Assert.ThrowsAsync<EntityStoreException>(() => store.DeleteAsync(stale));
        Assert.Equal(EntityErrorCode.Conflict, error.ErrorCode);
        Assert.Single(await store.GetAllAsync<TestClient>());
    }

    // ---------- Incremental synchronization (stores with SyncConventionsDecorator) ----------

    [Fact]
    public async Task Writes_set_LastModified()
    {
        if (!UsesSyncConventions)
            return;

        var before = DateTime.UtcNow.AddSeconds(-1);
        var client = await InsertAsync("Ana");
        var inserted = client.LastModified;
        Assert.True(inserted >= before);

        await Task.Delay(20);
        await using var store = CreateStore();
        await store.UpdateAsync(client);

        Assert.True(client.LastModified > inserted);
    }

    [Fact]
    public async Task Soft_deleted_rows_are_hidden_but_reported_as_changes()
    {
        if (!UsesSyncConventions)
            return;

        var a = await InsertAsync("Ana");
        await InsertAsync("Bob");

        await using var store = CreateStore();
        Assert.True(await store.DeleteAsync(a));

        Assert.Equal("Bob", Assert.Single(await store.GetAllAsync<TestClient>()).Name);
        Assert.Equal(1, await store.CountAsync<TestClient>(x => x.Active == true));
        Assert.Null(await store.FirstOrDefaultAsync<TestClient>(x => x.Id == a.Id));

        var changes = await store.SelectChangesAsync<TestClient>(DateTime.MinValue);
        Assert.True(Assert.Single(changes, c => c.Id == a.Id).Deleted);
    }

    [Fact]
    public async Task Changes_since_returns_only_recent_rows()
    {
        if (!UsesSyncConventions)
            return;

        await InsertAsync("Ancien");
        await Task.Delay(20);
        var recent = await InsertAsync("Recent");

        await using var store = CreateStore();
        var changes = await store.SelectChangesAsync<TestClient>(recent.LastModified);

        Assert.Equal("Recent", Assert.Single(changes).Name);
    }

    [Fact]
    public async Task Transaction_without_commit_is_rolled_back()
    {
        if (!SupportsTransactions)
            return;

        await using (var store = CreateStore())
        {
            await using var transaction = await store.BeginTransactionAsync();
            await store.InsertAsync(new TestClient { Name = "Rolled back" });
            // no CommitAsync
        }

        await using var check = CreateStore();
        Assert.Empty(await check.GetAllAsync<TestClient>());
    }

    [Fact]
    public async Task Committed_transaction_is_kept()
    {
        if (!SupportsTransactions)
            return;

        await using (var store = CreateStore())
        {
            var result = await store.RunInTransactionAsync(async s =>
            {
                await s.InsertAsync(new TestClient { Name = "Ana" });
                await s.InsertAsync(new TestClient { Name = "Bob" });
            });
            Assert.True(result.Successful);
        }

        await using var check = CreateStore();
        Assert.Equal(2, (await check.GetAllAsync<TestClient>()).Count);
    }
}

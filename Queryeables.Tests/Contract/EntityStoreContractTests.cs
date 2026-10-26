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

    /// <summary>True when the store applies [LastModified] and [SoftDelete] (<see cref="SyncConventionsStore"/>).</summary>
    protected virtual bool UsesSyncConventions => false;

    private async Task<TestClient> InsertAsync(string nom, bool actif = true, int? parrain = null)
    {
        var client = new TestClient { Nom = nom, Actif = actif, Parrain = parrain };
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

        Assert.Equal(new[] { "Ana", "Bob" }, all.Select(c => c.Nom).OrderBy(n => n));
    }

    [Fact]
    public async Task Select_filters_with_equalities()
    {
        await InsertAsync("Ana", actif: true, parrain: 1);
        await InsertAsync("Bob", actif: false, parrain: 1);
        await InsertAsync("Cid", actif: true, parrain: 2);

        await using var store = CreateStore();
        var result = await store.SelectAsync<TestClient>(c => c.Actif == true && c.Parrain == 1);

        Assert.Equal("Ana", Assert.Single(result).Nom);
    }

    [Fact]
    public async Task Select_handles_null_values()
    {
        await InsertAsync("Ana", parrain: null);
        await InsertAsync("Bob", parrain: 3);

        await using var store = CreateStore();
        var result = await store.SelectAsync<TestClient>(c => c.Parrain == null);

        Assert.Equal("Ana", Assert.Single(result).Nom);
    }

    [Fact]
    public async Task FirstOrDefault_returns_null_when_nothing_matches()
    {
        await InsertAsync("Ana");

        await using var store = CreateStore();

        Assert.Null(await store.FirstOrDefaultAsync<TestClient>(c => c.Nom == "Personne"));
        Assert.Equal("Ana", (await store.FirstOrDefaultAsync<TestClient>(c => c.Nom == "Ana"))?.Nom);
    }

    [Fact]
    public async Task SelectPaged_returns_stable_pages_starting_at_one()
    {
        foreach (var nom in new[] { "A", "B", "C", "D", "E" })
            await InsertAsync(nom);

        await using var store = CreateStore();
        var page1 = await store.SelectPagedAsync<TestClient>(c => c.Actif == true, 1, 2);
        var page2 = await store.SelectPagedAsync<TestClient>(c => c.Actif == true, 2, 2);
        var page3 = await store.SelectPagedAsync<TestClient>(c => c.Actif == true, 3, 2);

        var ids = page1.Concat(page2).Concat(page3).Select(c => c.Id).ToList();
        Assert.Equal(5, ids.Count);
        Assert.Equal(ids.OrderBy(id => id), ids); // sorted by key
        Assert.Single(page3);
    }

    [Fact]
    public async Task SelectPaged_rejects_page_zero()
    {
        await using var store = CreateStore();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.SelectPagedAsync<TestClient>(c => c.Actif == true, 0, 10));
    }

    [Fact]
    public async Task Update_returns_true_and_persists()
    {
        var client = await InsertAsync("Ana");
        client.Nom = "Ana modifiée";

        await using (var store = CreateStore())
            Assert.True(await store.UpdateAsync(client));

        await using var check = CreateStore();
        Assert.Equal("Ana modifiée", (await check.FirstOrDefaultAsync<TestClient>(c => c.Id == client.Id))?.Nom);
    }

    [Fact]
    public async Task Update_returns_false_for_a_missing_row()
    {
        await using var store = CreateStore();
        Assert.False(await store.UpdateAsync(new TestClient { Id = 999, Nom = "Fantôme" }));
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
        await InsertAsync("Ana", actif: true);
        await InsertAsync("Bob", actif: false);
        await InsertAsync("Cid", actif: true);

        await using var store = CreateStore();
        Assert.Equal(2, await store.CountAsync<TestClient>(c => c.Actif == true));
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

        Assert.Equal(new[] { "A", "C" }, result.Select(x => x.Nom).OrderBy(n => n));
    }

    [Fact]
    public async Task Select_with_text_searches()
    {
        await InsertAsync("Anaïs");
        await InsertAsync("Bob");
        await InsertAsync("Annabelle");

        await using var store = CreateStore();

        Assert.Equal(2, (await store.SelectAsync<TestClient>(x => x.Nom.StartsWith("An"))).Count);
        Assert.Equal("Annabelle", Assert.Single(await store.SelectAsync<TestClient>(x => x.Nom.EndsWith("belle"))).Nom);
        Assert.Equal("Bob", Assert.Single(await store.SelectAsync<TestClient>(x => x.Nom.Contains("o"))).Nom);
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
        client.Nom = "Modifiée par un autre";
        Assert.True(await store.UpdateAsync(client));

        stale.Nom = "Écrasement";
        var error = await Assert.ThrowsAsync<EntityStoreException>(() => store.UpdateAsync(stale));
        Assert.Equal(EntityErrorCode.Conflict, error.ErrorCode);
        Assert.Equal("Modifiée par un autre", (await store.FirstOrDefaultAsync<TestClient>(x => x.Id == client.Id))?.Nom);
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

    // ---------- Incremental synchronization (stores with SyncConventionsStore) ----------

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

        Assert.Equal("Bob", Assert.Single(await store.GetAllAsync<TestClient>()).Nom);
        Assert.Equal(1, await store.CountAsync<TestClient>(x => x.Actif == true));
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
        var recent = await InsertAsync("Récent");

        await using var store = CreateStore();
        var changes = await store.SelectChangesAsync<TestClient>(recent.LastModified);

        Assert.Equal("Récent", Assert.Single(changes).Nom);
    }

    [Fact]
    public async Task Transaction_without_commit_is_rolled_back()
    {
        if (!SupportsTransactions)
            return;

        await using (var store = CreateStore())
        {
            await using var transaction = await store.BeginTransactionAsync();
            await store.InsertAsync(new TestClient { Nom = "Annulé" });
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
            await store.RunInTransactionAsync(async s =>
            {
                await s.InsertAsync(new TestClient { Nom = "Ana" });
                await s.InsertAsync(new TestClient { Nom = "Bob" });
            });
        }

        await using var check = CreateStore();
        Assert.Equal(2, (await check.GetAllAsync<TestClient>()).Count);
    }
}

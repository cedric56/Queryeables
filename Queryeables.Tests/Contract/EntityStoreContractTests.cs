using Queryeables.Abstraction;

namespace Queryeables.Tests.Contract;

/// <summary>
/// Contrat commun à toutes les implémentations d'<see cref="IEntityStore"/>.
/// Pour une nouvelle implémentation : dériver de cette classe et fournir <see cref="CreateStoreAsync"/>.
/// Chaque test part d'une base vide qui contient la table Clients.
/// </summary>
public abstract class EntityStoreContractTests : IAsyncLifetime
{
    /// <summary>Prépare une base vide (table Clients créée).</summary>
    public abstract Task InitializeAsync();

    public abstract Task DisposeAsync();

    /// <summary>Crée un store sur la base du test. Le test le dispose.</summary>
    protected abstract IEntityStore CreateStore();

    /// <summary>False pour un store qui ne prend pas en charge les transactions (le store distant).</summary>
    protected virtual bool SupportsTransactions => true;

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
        Assert.Equal(ids.OrderBy(id => id), ids); // triés par clé
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
    public async Task Transaction_without_commit_is_rolled_back()
    {
        if (!SupportsTransactions)
            return;

        await using (var store = CreateStore())
        {
            await using var transaction = await store.BeginTransactionAsync();
            await store.InsertAsync(new TestClient { Nom = "Annulé" });
            // pas de CommitAsync
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

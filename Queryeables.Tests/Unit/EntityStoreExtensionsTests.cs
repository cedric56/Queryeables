using Queryeables.Abstraction;

namespace Queryeables.Tests.Unit;

public class EntityStoreExtensionsTests
{
    [Fact]
    public async Task SyncChildren_deletes_then_updates_then_inserts()
    {
        var log = new List<string>();
        var rows = new List<TestClient> { new() { Id = 1, Nom = "A" }, new() { Id = 2, Nom = "B" }, new() { Id = 3, Nom = "C" } };
        var store = new InMemoryEntityStore(rows, log);

        var before = rows.Select(r => r.Copy()).ToList();
        var current = new List<TestClient>
        {
            new() { Id = 1, Nom = "A modifié" },
            new() { Id = 3, Nom = "C" },
            new() { Id = 0, Nom = "Nouveau" },
        };

        await store.RunInTransactionAsync(s => s.SyncChildrenAsync(before, current, c => c.Id == 0, (a, b) => a.Id == b.Id));

        Assert.Equal(new[] { "BEGIN", "DELETE 2", "UPDATE 1", "UPDATE 3", "INSERT Nouveau", "COMMIT", "DISPOSE" }, log);
        Assert.Equal(new[] { "A modifié", "C", "Nouveau" }, rows.Select(r => r.Nom));
    }

    [Fact]
    public async Task RunInTransaction_does_not_commit_on_error_and_rethrows()
    {
        var log = new List<string>();
        var store = new InMemoryEntityStore([], log);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RunInTransactionAsync(_ => throw new InvalidOperationException("boom")));

        Assert.Equal("boom", error.Message);
        Assert.DoesNotContain("COMMIT", log);
        Assert.Contains("DISPOSE", log);
    }

    [Fact]
    public async Task Default_CountAsync_counts_through_SelectAsync()
    {
        IEntityStore store = new InMemoryEntityStore(
            [new() { Id = 1, Actif = true }, new() { Id = 2, Actif = false }, new() { Id = 3, Actif = true }]);

        Assert.Equal(2, await store.CountAsync<TestClient>(c => c.Actif));
    }
}

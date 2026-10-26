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

        var result = await store.RunInTransactionAsync(s => s.SyncChildrenAsync(before, current, c => c.Id == 0, (a, b) => a.Id == b.Id));

        Assert.True(result.Successful);

        Assert.Equal(new[] { "BEGIN", "DELETE 2", "UPDATE 1", "UPDATE 3", "INSERT Nouveau", "COMMIT", "DISPOSE" }, log);
        Assert.Equal(new[] { "A modifié", "C", "Nouveau" }, rows.Select(r => r.Nom));
    }

    [Fact]
    public async Task RunInTransaction_does_not_commit_on_error_and_returns_a_failure()
    {
        var log = new List<string>();
        var store = new InMemoryEntityStore([], log);

        var result = await store.RunInTransactionAsync(_ => throw new InvalidOperationException("boom"));

        Assert.False(result.Successful);
        Assert.Equal(EntityErrorCode.Unknown, result.ErrorCode);
        Assert.Equal("boom", result.Exception);
        Assert.DoesNotContain("COMMIT", log);
        Assert.Contains("DISPOSE", log);
    }

    [Fact]
    public async Task RunInTransaction_keeps_the_error_kind_of_a_store_exception()
    {
        var store = new InMemoryEntityStore([]);

        var result = await store.RunInTransactionAsync(_ =>
            throw new EntityStoreException(EntityErrorCode.Validation, "invalide", ["Nom requis"]));

        Assert.Equal(EntityErrorCode.Validation, result.ErrorCode);
        Assert.Equal(new[] { "Nom requis" }, result.Errors);
        Assert.Throws<EntityStoreException>(result.ThrowIfFailed);
    }

    [Fact]
    public async Task RunInTransaction_returns_the_value_once_committed()
    {
        var log = new List<string>();
        var store = new InMemoryEntityStore([], log);

        var result = await store.RunInTransactionAsync(async s =>
        {
            var client = new TestClient { Nom = "Ana" };
            await s.InsertAsync(client);
            return client.Nom;
        });

        Assert.Equal("Ana", result.GetDataOrThrow());
        Assert.Contains("COMMIT", log);
    }

    [Fact]
    public async Task RunInTransaction_lets_cancellation_propagate()
    {
        var store = new InMemoryEntityStore([]);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.RunInTransactionAsync(_ => throw new OperationCanceledException()));
    }

    [Fact]
    public async Task OrThrow_turns_a_failure_back_into_an_exception()
    {
        var store = new InMemoryEntityStore([]);

        var error = await Assert.ThrowsAsync<EntityStoreException>(() =>
            store.RunInTransactionAsync(_ => throw new EntityStoreException(EntityErrorCode.Conflict, "conflit")).OrThrow());

        Assert.Equal(EntityErrorCode.Conflict, error.ErrorCode);
    }

    [Fact]
    public async Task Default_CountAsync_counts_through_SelectAsync()
    {
        IEntityStore store = new InMemoryEntityStore(
            [new() { Id = 1, Actif = true }, new() { Id = 2, Actif = false }, new() { Id = 3, Actif = true }]);

        Assert.Equal(2, await store.CountAsync<TestClient>(c => c.Actif));
    }
}

using Microsoft.Extensions.DependencyInjection;
using Queryeables.Abstraction;
using Queryeables.Remote;
using Queryeables.Remote.Offline;
using System.Security.Claims;

namespace Queryeables.Tests.Unit;

public sealed class Folder
{
    public long Id { get; set; }
    public string Owner { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<Note>? Notes { get; set; }
}

public sealed class Note
{
    public long Id { get; set; }
    public long FolderId { get; set; }
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// A client that tries to reach the rows of another user: through a graph, by key, with conditions written by hand, or
/// after a change of user on the same machine. Each user owns folders; the notes of a folder follow it.
/// </summary>
public class HostileClientTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();
    private readonly EntityRequestDispatcher _dispatcher;
    private readonly Folder _anaFolder;
    private readonly Note _anaNote;

    public HostileClientTests()
    {
        var stores = new EntityStoreFactory(() => new MemoryStore(_database));
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<Folder>(stores, EntityAccessPolicies.Authenticated,
            aggregate: new GraphRepository<Folder, long>(stores, Graph()),
            rows: RowLevelSecurity.For<Folder, long>(f => f.Id, user =>
            {
                var name = NameOf(user);
                return f => f.Owner == name;
            })));
        // The notes are exposed too: their filter follows the folders of the user.
        registry.Register(new EntityHandler<Note>(stores, EntityAccessPolicies.Authenticated,
            rows: RowLevelSecurity.For<Note, long>(n => n.Id, user =>
            {
                var name = NameOf(user);
                var folders = _database.All<Folder>().Where(f => f.Owner == name).Select(f => f.Id).ToList();
                return n => folders.Contains(n.FolderId);
            })));
        _dispatcher = new EntityRequestDispatcher(registry, _serializer, stores);

        var server = new MemoryStore(_database);
        _anaFolder = new Folder { Owner = "ana", Name = "Ana's" };
        server.InsertAsync(_anaFolder).GetAwaiter().GetResult();
        server.InsertAsync(new Folder { Owner = "bob", Name = "Bob's" }).GetAwaiter().GetResult();
        _anaNote = new Note { FolderId = _anaFolder.Id, Text = "secret" };
        server.InsertAsync(_anaNote).GetAwaiter().GetResult();
        server.InsertAsync(new Note { FolderId = 2, Text = "bob's note" }).GetAwaiter().GetResult();
    }

    private static EntityGraph<Folder, long> Graph()
        => EntityGraph.For<Folder, long>(f => f.Id).HasMany(f => f.Notes, foreignKey: n => n.FolderId, key: n => n.Id);

    private static string? NameOf(ClaimsPrincipal? user) => user?.Identity?.Name;

    private static EntityRequestContext As(string user)
        => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test")));

    private IEntityStoreFactory Stores(IEntityTransport transport) => RemoteEntityStore.Factory(transport, _serializer);

    private IEntityStoreFactory Bob => Stores(_dispatcher.AsTransport(As("bob")));

    private T[] Read<T>(TransportResult result) => _serializer.Deserialize<T[]>(result.GetDataOrThrow()!)!;

    // ---------- Graphs ----------

    [Fact]
    public async Task A_child_of_another_user_cannot_be_moved_into_ones_own_parent()
    {
        var bobs = new GraphRepository<Folder, long>(Bob, Graph());
        var bobFolder = new Folder { Id = 2, Owner = "bob", Name = "Bob's", Notes = [new Note { Id = _anaNote.Id, Text = "mine now" }] };

        var result = await bobs.SaveAsync(bobFolder);

        Assert.False(result.Successful);
        var note = _database.All<Note>().Single(n => n.Id == _anaNote.Id);
        Assert.Equal("secret", note.Text);
        Assert.Equal(_anaFolder.Id, note.FolderId);
    }

    [Fact]
    public async Task The_parent_of_another_user_cannot_be_saved_nor_removed_with_its_children()
    {
        var bobs = new GraphRepository<Folder, long>(Bob, Graph());
        var stolen = new Folder { Id = _anaFolder.Id, Owner = "bob", Name = "Taken", Notes = [] };

        var save = await bobs.SaveAsync(stolen);
        var remove = await bobs.RemoveAsync(new Folder { Id = _anaFolder.Id, Owner = "ana" });

        Assert.Equal(EntityErrorCode.NotFound, save.ErrorCode);
        Assert.Equal(EntityErrorCode.NotFound, remove.ErrorCode);
        Assert.Equal("Ana's", _database.All<Folder>().Single(f => f.Id == _anaFolder.Id).Name);
        Assert.Contains(_database.All<Note>(), n => n.Id == _anaNote.Id);
    }

    [Fact]
    public async Task Children_read_directly_are_filtered_too()
    {
        await using var store = Bob.Create();

        Assert.Equal(["bob's note"], (await store.GetAllAsync<Note>()).Select(n => n.Text));
        Assert.Null(await store.FirstOrDefaultAsync<Note>(n => n.Id == _anaNote.Id));
        var write = await Assert.ThrowsAsync<EntityStoreException>(() => store.UpdateAsync(new Note { Id = _anaNote.Id, FolderId = 2, Text = "x" }));
        Assert.Equal(EntityErrorCode.NotFound, write.ErrorCode);
    }

    // ---------- Requests written by hand ----------

    [Fact]
    public async Task Conditions_written_by_hand_cannot_widen_the_rows_of_the_user()
    {
        var bob = _dispatcher.AsTransport(As("bob"));
        var folder = EntityName.Of<Folder>();

        // "Id >= 0", combined with OR: every row, if the filter of the user were only one more condition.
        var everything = Read<Folder>(await bob.SelectAsync(folder, [new FilterCondition("Id", FilterOperator.GreaterThanOrEqual, 0L)], any: true));
        var asAna = Read<Folder>(await bob.SelectAsync(folder, [new FilterCondition("Owner", FilterOperator.Equal, "ana")], any: false));
        var sorted = Read<Folder>(await bob.SelectPagedAsync(folder, [], false, 1, 100, [new SortKey("Owner")]));
        var changes = await bob.GetChangesAsync(folder, DateTime.MinValue);

        Assert.Equal(["bob"], everything.Select(f => f.Owner));
        Assert.Empty(asAna);
        Assert.Equal(["bob"], sorted.Select(f => f.Owner));
        Assert.False(changes.Successful);      // no [LastModified]: refused, and nothing read
    }

    [Fact]
    public async Task A_property_or_a_type_that_is_not_exposed_is_refused()
    {
        var bob = _dispatcher.AsTransport(As("bob"));

        var unknownProperty = await bob.SelectAsync(EntityName.Of<Folder>(), [new FilterCondition("Secret", FilterOperator.Equal, "x")], false);
        var unknownSort = await bob.SelectPagedAsync(EntityName.Of<Folder>(), [], false, 1, 10, [new SortKey("Secret")]);
        var unknownType = await bob.GetAllAsync("System.IO.File");

        Assert.Equal(EntityErrorCode.InvalidRequest, unknownProperty.ErrorCode);
        Assert.Equal(EntityErrorCode.InvalidRequest, unknownSort.ErrorCode);
        Assert.Equal(EntityErrorCode.UnknownType, unknownType.ErrorCode);
    }

    [Fact]
    public async Task An_anonymous_call_reaches_nothing()
    {
        var anonymous = _dispatcher.AsTransport();

        var result = await anonymous.GetAllAsync(EntityName.Of<Folder>());

        Assert.Equal(EntityErrorCode.Forbidden, result.ErrorCode);
    }

    // ---------- A change of user on the same client ----------

    [Fact]
    public async Task A_cache_forgets_the_previous_user_before_loading_for_the_next()
    {
        var transport = new SwitchingTransport(_dispatcher, "ana");
        var cache = new EntityCache<Folder, long>(Stores(transport), f => f.Id);
        await cache.LoadAsync();
        Assert.Equal(["ana"], cache.Entities.Select(f => f.Owner));

        transport.User = "bob";
        await UserScopedData.ChangeUserAsync([cache], reload: false);
        Assert.Empty(cache.Entities);          // nothing of Ana, even before the reload

        await UserScopedData.ChangeUserAsync([cache]);
        Assert.Equal(["bob"], cache.Entities.Select(f => f.Owner));
    }

    [Fact]
    public async Task The_offline_copy_of_the_previous_user_is_forgotten_and_its_writes_are_never_sent_as_the_next()
    {
        var transport = new SwitchingTransport(_dispatcher, "ana");
        var storage = new MemoryOfflineStorage();
        await using var mode = new OfflineMode(transport, _serializer, storage).Add<Folder, long>(f => f.Id).Add<Note, long>(n => n.Id);
        var stores = new OfflineStoreFactory(Stores(transport), mode);
        await mode.GoOfflineAsync();
        await using (var store = stores.Create())
            await store.InsertAsync(new Note { FolderId = _anaFolder.Id, Text = "written offline by Ana" });

        transport.User = "bob";
        await Assert.ThrowsAsync<InvalidOperationException>(() => UserScopedData.ChangeUserAsync([mode]));
        Assert.Equal(1, mode.Status.PendingChanges);        // refused: nothing forgotten, nothing sent

        await mode.DiscardPendingChangesAsync();
        await UserScopedData.ChangeUserAsync([mode]);
        Assert.Equal(ConnectionMode.Online, mode.Mode);
        Assert.Null(storage.ReadAsync($"{mode.StoragePrefix}/{EntityName.Of<Note>()}/row/{_anaNote.Id}").GetAwaiter().GetResult());

        await mode.GoOfflineAsync();
        await using var bobStore = stores.Create();
        Assert.Equal(["bob's note"], (await bobStore.GetAllAsync<Note>()).Select(n => n.Text));
        Assert.DoesNotContain("written offline by Ana", _database.All<Note>().Select(n => n.Text));
    }

    [Fact]
    public async Task ChangeUserAsync_reaches_every_registered_cache()
    {
        var transport = new SwitchingTransport(_dispatcher, "ana");
        var services = new ServiceCollection();
        services.UseQueryeables(q => q.UseStores(_ => Stores(transport)).AddCache<Folder, long>(f => f.Id).AddCache<Note, long>(n => n.Id));
        await using var provider = services.BuildServiceProvider();
        var folders = provider.GetRequiredService<IEntityCache<Folder, long>>();
        var notes = provider.GetRequiredService<IEntityCache<Note, long>>();
        await folders.LoadAsync();

        transport.User = "bob";
        await provider.ChangeUserAsync();

        Assert.Equal(["bob"], folders.Entities.Select(f => f.Owner));
        Assert.Empty(notes.Entities);          // never loaded: not loaded now either
    }

    /// <summary>The transport of one client, whose signed-in user changes.</summary>
    private sealed class SwitchingTransport(EntityRequestDispatcher dispatcher, string user) : IEntityTransport
    {
        public string User { get; set; } = user;

        private IEntityTransport Current => dispatcher.AsTransport(As(User));

        public Task<TransportResult> GetAllAsync(string type, CancellationToken ct = default) => Current.GetAllAsync(type, ct);

        public Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, bool any, CancellationToken ct = default)
            => Current.SelectAsync(type, conditions, any, ct);

        public Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
            CancellationToken ct = default)
            => Current.SelectPagedAsync(type, conditions, any, pageNumber, pageSize, ct);

        public Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
            SortKey[] sort, CancellationToken ct = default)
            => Current.SelectPagedAsync(type, conditions, any, pageNumber, pageSize, sort, ct);

        public Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default)
            => Current.GetChangesAsync(type, sinceUtc, ct);

        public Task<TransportResult> InsertAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
            => Current.InsertAsync(type, entity, connectionId, ct);

        public Task<TransportResult> UpdateAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
            => Current.UpdateAsync(type, entity, connectionId, ct);

        public Task<TransportResult> DeleteAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default)
            => Current.DeleteAsync(type, entity, connectionId, ct);

        public Task<TransportResult> ExecuteBatchAsync(BatchOperation[] operations, CancellationToken ct = default)
            => Current.ExecuteBatchAsync(operations, ct);
    }
}

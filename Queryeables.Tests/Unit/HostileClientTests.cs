using Microsoft.Extensions.DependencyInjection;
using Queryeables.Abstraction;
using Queryeables.Remote;
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
/// A client that tries to reach the rows of another user: by key, with conditions written by hand, or after a change of user
/// on the same machine. Each user owns folders; the notes of a folder follow it.
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
        IEntityStoreFactory stores = new EntityStoreFactory(() => new MemoryStore(_database));
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<Folder>(stores, EntityAccessPolicies.Authenticated,
            RowLevelSecurity.For<Folder, long>(f => f.Id, user =>
            {
                var name = NameOf(user);
                return f => f.Owner == name;
            })));
        // The notes are exposed too: their filter follows the folders of the user.
        registry.Register(new EntityHandler<Note>(stores, EntityAccessPolicies.Authenticated,
            RowLevelSecurity.For<Note, long>(n => n.Id, user =>
            {
                var name = NameOf(user);
                var folders = _database.All<Folder>().Where(f => f.Owner == name).Select(f => f.Id).ToList();
                return n => folders.Contains(n.FolderId);
            })));
        _dispatcher = new EntityRequestDispatcher(registry, _serializer);

        var server = new MemoryStore(_database);
        _anaFolder = new Folder { Owner = "ana", Name = "Ana's" };
        server.InsertAsync(_anaFolder).GetAwaiter().GetResult();
        server.InsertAsync(new Folder { Owner = "bob", Name = "Bob's" }).GetAwaiter().GetResult();
        _anaNote = new Note { FolderId = _anaFolder.Id, Text = "secret" };
        server.InsertAsync(_anaNote).GetAwaiter().GetResult();
        server.InsertAsync(new Note { FolderId = 2, Text = "bob's note" }).GetAwaiter().GetResult();
    }

    private static string? NameOf(ClaimsPrincipal? user) => user?.Identity?.Name;

    private static EntityRequestContext As(string user)
        => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test")));

    private IEntityStoreFactory Stores(IEntityTransport transport) => RemoteEntityStore.Factory(transport, _serializer);

    private IEntityStoreFactory Bob => Stores(_dispatcher.AsTransport(As("bob")));

    [Fact]
    public async Task Rows_read_directly_or_by_key_are_those_of_the_user()
    {
        await using var store = Bob.Create();

        Assert.Equal(["bob's note"], (await store.GetAllAsync<Note>()).Select(n => n.Text));
        var anaNote = _anaNote.Id;
        Assert.Null(await store.FirstOrDefaultAsync<Note>(n => n.Id == anaNote));
    }

    [Fact]
    public async Task Conditions_written_by_hand_are_refused()
    {
        var bob = _dispatcher.AsTransport(As("bob"));
        var folder = EntityName.Of<Folder>();

        var asAna = await bob.SelectAsync(folder, [new FilterCondition("Owner", FilterOperator.Equal, "ana")]);
        var twice = await bob.SelectAsync(folder, [new FilterCondition("Id", FilterOperator.Equal, 1L), new FilterCondition("Id", FilterOperator.Equal, 2L)]);

        Assert.Equal(EntityErrorCode.InvalidRequest, asAna.ErrorCode);
        Assert.Equal(EntityErrorCode.InvalidRequest, twice.ErrorCode);
    }

    [Fact]
    public async Task An_anonymous_call_reaches_nothing()
    {
        var result = await _dispatcher.AsTransport().GetAllAsync(EntityName.Of<Folder>());

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

        public Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, CancellationToken ct = default)
            => Current.SelectAsync(type, conditions, ct);

        public Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default)
            => Current.GetChangesAsync(type, sinceUtc, ct);
    }
}

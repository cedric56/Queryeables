using Microsoft.Extensions.DependencyInjection;
using Queryeables.Abstraction;
using Queryeables.Offline;
using Queryeables.Remote;
using System.Security.Claims;

namespace Queryeables.Tests.Unit;

/// <summary>
/// The core without Queryeables.Offline: a server publishes replicas (the rows each user may read, the changes, rows by key),
/// a client reads them and nothing else. Then the extension points the offline package plugs into.
/// </summary>
public class ReplicaTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();

    private IEntityStoreFactory Stores => new EntityStoreFactory(() => new MemoryStore(_database));

    private static EntityRequestContext As(string user)
        => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test")));

    /// <summary>A server whose users read the active rows only.</summary>
    private EntityRequestDispatcher Server()
    {
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<TestClient>(Stores, EntityAccessPolicies.Authenticated,
            RowLevelSecurity.For<TestClient, long>(c => c.Id, _ => c => c.Active)));
        return new EntityRequestDispatcher(registry, _serializer, Stores);
    }

    private async Task SeedAsync()
    {
        await using var store = new MemoryStore(_database);
        await store.InsertAsync(new TestClient { Name = "Ana", Active = true });
        await store.InsertAsync(new TestClient { Name = "Bob", Active = false });
    }

    [Fact]
    public async Task A_replica_holds_the_rows_its_user_may_read_and_a_row_by_key()
    {
        await SeedAsync();
        await using var store = new RemoteEntityStore(Server().AsTransport(As("ana")), _serializer);

        var all = await store.GetAllAsync<TestClient>();
        Assert.Equal("Ana", Assert.Single(all).Name);
        var anaId = all[0].Id;
        Assert.Equal("Ana", (await store.FirstOrDefaultAsync<TestClient>(c => c.Id == anaId))?.Name);
        var bobId = _database.All<TestClient>().Single(c => c.Name == "Bob").Id;
        Assert.Null(await store.FirstOrDefaultAsync<TestClient>(c => c.Id == bobId));      // outside the user's rows
    }

    [Fact]
    public async Task A_replica_store_refuses_queries_and_writes()
    {
        await SeedAsync();
        await using var store = new RemoteEntityStore(Server().AsTransport(As("ana")), _serializer);

        var query = await Assert.ThrowsAsync<EntityStoreException>(() => store.SelectAsync<TestClient>(c => c.Name == "Ana"));
        var write = await Assert.ThrowsAsync<EntityStoreException>(() => store.InsertAsync(new TestClient { Name = "Eve" }));

        Assert.Equal(EntityErrorCode.NotSupported, query.ErrorCode);
        Assert.Equal(EntityErrorCode.NotSupported, write.ErrorCode);
        Assert.Equal(2, _database.All<TestClient>().Count);
    }

    [Fact]
    public async Task A_replica_server_refuses_writes_and_filters_a_client_sends_anyway()
    {
        await SeedAsync();
        var transport = Server().AsTransport(As("ana"));

        var insert = await transport.InsertAsync(EntityName.Of<TestClient>(), _serializer.Serialize(new TestClient { Name = "Eve" }), null);
        var filter = await transport.SelectAsync(EntityName.Of<TestClient>(), [new FilterCondition("Name", FilterOperator.StartsWith, "B")], false);

        Assert.Equal(EntityErrorCode.NotSupported, insert.ErrorCode);
        Assert.Equal(EntityErrorCode.InvalidRequest, filter.ErrorCode);
        Assert.Equal(2, _database.All<TestClient>().Count);
    }

    [Fact]
    public async Task A_row_that_leaves_the_users_rows_leaves_the_replica_at_its_notification()
    {
        await SeedAsync();
        var feed = new ChangeFeed<TestClient>();
        using var replica = new EntityCache<TestClient, long>(RemoteEntityStore.Factory(Server().AsTransport(As("ana")), _serializer),
            c => c.Id, feed, EntityKey.Parse<long>);
        await replica.LoadAsync();
        var ana = Assert.Single(replica.Entities);

        await using (var server = new MemoryStore(_database))
        {
            var stored = _database.All<TestClient>().Single(c => c.Id == ana.Id);
            stored.Active = false;                                              // no longer one of this user's rows
            await server.UpdateAsync(stored);
        }
        var changed = new TaskCompletionSource();
        replica.EntitiesChanged += (_, _) => changed.TrySetResult();
        feed.Publish(new EntityChange<TestClient>(ana.Id, ChangeType.Update));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(replica.Entities);
    }

    [Fact]
    public async Task Too_many_rows_for_a_replica_are_refused_not_cut()
    {
        await SeedAsync();
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<TestClient>(Stores, EntityAccessPolicies.AllowAll, maxReadSize: 1));
        await using var store = new RemoteEntityStore(new EntityRequestDispatcher(registry, _serializer, Stores).AsTransport(), _serializer);

        var refused = await Assert.ThrowsAsync<EntityStoreException>(() => store.GetAllAsync<TestClient>());

        Assert.Equal(EntityErrorCode.InvalidRequest, refused.ErrorCode);
    }

    // ---------- The extension points ----------

    [Fact]
    public void The_core_assemblies_never_name_the_offline_package()
    {
        Type[] core = [typeof(IEntityStore), typeof(EntityRequestDispatcher), typeof(QueryeablesBuilder)];
        foreach (var assembly in core.Select(t => t.Assembly))
        {
            var offline = typeof(OfflineMode).Assembly.GetName().Name;
            Assert.DoesNotContain(offline, assembly.GetReferencedAssemblies().Select(name => name.Name));
            Assert.Empty(assembly.GetTypes().Where(type => type.Namespace?.StartsWith("Queryeables.Offline", StringComparison.Ordinal) == true));
        }
    }

    [Fact]
    public async Task DecorateStores_wraps_the_stores_before_the_caches_are_told()
    {
        var wrapped = 0;
        var services = new ServiceCollection();
        services.UseQueryeables(q => q
            .UseStores(_ => Stores)
            .DecorateStores((_, factory) => new EntityStoreFactory(() => { wrapped++; return factory.Create(); }, factory.IsRemote)));
        await using var provider = services.BuildServiceProvider();

        await using (var store = provider.GetRequiredService<IEntityStoreFactory>().Create())
            await store.InsertAsync(new TestClient { Name = "Ana" });

        Assert.Equal(1, wrapped);
        Assert.Single(_database.All<TestClient>());
    }

    [Fact]
    public async Task A_server_serves_replicas_unless_writes_are_allowed()
    {
        async Task<TransportResult> InsertThrough(Action<EntityServerBuilder> expose)
        {
            var services = new ServiceCollection();
            services.UseQueryeables(q => q.UseStores(_ => Stores).AddEntityServer(_serializer, expose));
            await using var provider = services.BuildServiceProvider();
            return await provider.GetRequiredService<EntityRequestDispatcher>().AsTransport()
                .InsertAsync(EntityName.Of<TestClient>(), _serializer.Serialize(new TestClient { Name = "Eve" }), null);
        }

        var replica = await InsertThrough(server => server.Expose<TestClient>(EntityAccessPolicies.AllowAll));
        var writable = await InsertThrough(server => server.AllowWrites().Expose<TestClient>(EntityAccessPolicies.AllowAll));

        Assert.Equal(EntityErrorCode.NotSupported, replica.ErrorCode);
        Assert.True(writable.Successful, writable.Exception);
        Assert.Single(_database.All<TestClient>());
    }

    [Fact]
    public async Task UseRemoteWrites_makes_a_remote_client_write_through_the_server()
    {
        var registry = new EntityHandlerRegistry();
        registry.Register(new WritableEntityHandler<TestClient>(Stores, EntityAccessPolicies.AllowAll));
        var transport = new EntityRequestDispatcher(registry, _serializer, Stores).AsTransport();
        var services = new ServiceCollection();
        services.AddSingleton(transport);
        services.UseQueryeables(q => q
            .UseStores(_ => RemoteEntityStore.Factory(transport, _serializer))
            .UseRemoteWrites());
        services.AddSingleton<IEntitySerializer>(_serializer);
        await using var provider = services.BuildServiceProvider();

        await using (var store = provider.GetRequiredService<IEntityStoreFactory>().Create())
            await store.InsertAsync(new TestClient { Name = "Ana" });

        Assert.Equal("Ana", Assert.Single(_database.All<TestClient>()).Name);
    }
}

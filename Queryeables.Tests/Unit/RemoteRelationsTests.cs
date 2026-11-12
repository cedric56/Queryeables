using System.Security.Claims;
using Queryeables.Abstraction;
using Queryeables.Remote;
using Queryeables.Remote.Offline;
using Queryeables.Tests.Graphs;

namespace Queryeables.Tests.Unit;

/// <summary>
/// The same relations through a server: filters through them, counts, aggregates, projections, patches, writes of many rows,
/// graphs saved with the lines a user did not touch left alone, and keys created offline across types.
/// </summary>
public sealed class RemoteRelationsTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();

    private IEntityStoreFactory Stores => new EntityStoreFactory(() => new MemoryStore(_database));

    private EntityRequestDispatcher Server(bool exposeTags = true, IEntityAccessPolicy? clients = null)
    {
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<GInvoice>(Stores, EntityAccessPolicies.AllowAll,
            aggregate: new GraphRepository<GInvoice, long>(Stores, GInvoiceGraph.Create())));
        registry.Register(new EntityHandler<GInvoiceLine>(Stores, EntityAccessPolicies.AllowAll));
        registry.Register(new EntityHandler<GLineTax>(Stores, EntityAccessPolicies.AllowAll));
        registry.Register(new EntityHandler<GInvoiceNote>(Stores, EntityAccessPolicies.AllowAll));
        registry.Register(new EntityHandler<GClient>(Stores, clients ?? EntityAccessPolicies.AllowAll));
        if (exposeTags)
        {
            registry.Register(new EntityHandler<GTag>(Stores, EntityAccessPolicies.AllowAll));
            registry.Register(new EntityHandler<GInvoiceTag>(Stores, EntityAccessPolicies.AllowAll));
        }
        return new EntityRequestDispatcher(registry, _serializer, Stores);
    }

    private static EntityRequestContext User(string name = "ana")
        => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, name)], "test")));

    private RemoteEntityStore Client(EntityRequestDispatcher? server = null) => new((server ?? Server()).AsTransport(User()), _serializer);

    private IEntityStoreFactory ClientStores(EntityRequestDispatcher server)
        => RemoteEntityStore.Factory(server.AsTransport(User()), _serializer);

    private async Task SeedAsync()
    {
        await using var store = new MemoryStore(_database);
        var urgent = new GTag { Name = "urgent" };
        var paris = new GClient { Name = "Ana", City = "Paris" };
        var lyon = new GClient { Name = "Bob", City = "Lyon" };
        await store.InsertAsync(urgent);
        await store.InsertAsync(paris);
        await store.InsertAsync(lyon);
        var invoices = new GraphRepository<GInvoice, long>(Stores, GInvoiceGraph.Create());
        await invoices.SaveAsync(new GInvoice
        {
            Number = "A", Year = 2025, GClientId = paris.Id, Tags = [urgent],
            Lines = [new GInvoiceLine { Product = "tea", Quantity = 2, Price = 3m, Taxes = [new GLineTax { Rate = 0.2m }] }, new GInvoiceLine { Product = "cake", Quantity = 1, Price = 5m, Taxes = [] }],
        });
        await invoices.SaveAsync(new GInvoice
        {
            Number = "B", Year = 2026, GClientId = lyon.Id, Tags = [],
            Lines = [new GInvoiceLine { Product = "tea", Quantity = 5, Price = 2m, Taxes = [] }],
        });
        await invoices.SaveAsync(new GInvoice { Number = "C", Year = 2026, Lines = [], Tags = [] });
    }

    private static async Task<string[]> Numbers(IEntityStore store, System.Linq.Expressions.Expression<Func<GInvoice, bool>> predicate)
        => [.. (await store.SelectAsync(predicate)).Select(i => i.Number).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task A_remote_filter_goes_through_collections_navigations_links_and_computations()
    {
        await SeedAsync();
        await using var store = Client();
        var product = "cake";

        Assert.Equal(["A"], await Numbers(store, i => i.Lines!.Any(l => l.Product == product)));
        Assert.Equal(["C"], await Numbers(store, i => !i.Lines!.Any()));
        Assert.Equal(["A"], await Numbers(store, i => i.Lines!.Count() >= 2));
        Assert.Equal(["B"], await Numbers(store, i => i.Lines!.All(l => l.Quantity * l.Price >= 10m) && i.Lines!.Any()));
        Assert.Equal(["A"], await Numbers(store, i => i.GClient!.City.ToUpper() == "PARIS"));
        Assert.Equal(["C"], await Numbers(store, i => i.GClient == null));
        Assert.Equal(["A"], await Numbers(store, i => i.Tags!.Any(t => t.Name.StartsWith("urg"))));
        Assert.Equal(["A"], await Numbers(store, i => i.Lines!.Any(l => l.Taxes!.Any(t => t.Rate > 0.1m))));
        Assert.Equal(["B", "C"], await Numbers(store, i => i.Year > 2025 && i.Number.Length == 1));
    }

    [Fact]
    public async Task A_relation_toward_a_type_the_server_does_not_expose_cannot_be_filtered_on()
    {
        await SeedAsync();
        await using var store = Client(Server(exposeTags: false));
        var error = await Assert.ThrowsAsync<EntityStoreException>(() => store.SelectAsync<GInvoice>(i => i.Tags!.Any(t => t.Name == "urgent")));
        Assert.Equal(EntityErrorCode.InvalidRequest, error.ErrorCode);
    }

    [Fact]
    public async Task A_relation_toward_a_type_the_user_may_not_read_is_refused()
    {
        await SeedAsync();
        var noClients = EntityAccessPolicies.From(r => r.Operation != EntityOperation.Read || r.EntityType != typeof(GClient));
        await using var store = Client(Server(clients: noClients));
        var error = await Assert.ThrowsAsync<EntityStoreException>(() => store.SelectAsync<GInvoice>(i => i.GClient!.City == "Paris"));
        Assert.Equal(EntityErrorCode.Forbidden, error.ErrorCode);
    }

    [Fact]
    public async Task Counts_aggregates_and_projections_are_computed_by_the_server()
    {
        await SeedAsync();
        await using var store = Client();

        Assert.Equal(2L, await store.CountAsync<GInvoiceLine>(l => l.Product == "tea"));
        Assert.Equal(21m, await store.SumAsync<GInvoiceLine, decimal>(l => true, l => l.Price * l.Quantity));
        Assert.Equal(5, await store.MaxAsync<GInvoiceLine, int>(l => true, l => l.Quantity));
        Assert.Equal(2026, await store.MaxAsync<GInvoice, int>(i => i.Lines!.Any(), i => i.Year));

        var perProduct = await store.GroupAsync<GInvoiceLine, string>(l => true, l => l.Product, a => a.Sum(l => l.Quantity).Average(l => l.Price));
        var tea = Assert.Single(perProduct, g => g.Key == "tea");
        Assert.Equal((2L, 7L, 2.5m), (tea.Count, (long)tea.Values[0]!, (decimal)tea.Values[1]!));

        var names = await store.SelectAsync<GClient, string>(c => c.City != "", c => c.Name + " (" + c.City + ")");
        Assert.Equal(["Ana (Paris)", "Bob (Lyon)"], names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Two_users_patching_different_properties_of_one_row_keep_both_changes()
    {
        await SeedAsync();
        await using var store = Client();
        var read = (await store.SelectAsync<GClient>(c => c.Name == "Ana")).Single();
        var ana = EntityValues.Copy(read);
        var bob = EntityValues.Copy(read);

        ana.City = "Nice";
        Assert.True(await store.PatchAsync(read, ana));
        bob.Name = "Anna";
        Assert.True(await store.PatchAsync(read, bob));

        var stored = _database.All<GClient>().Single(c => c.Id == read.Id);
        Assert.Equal(("Anna", "Nice"), (stored.Name, stored.City));
        Assert.Equal("Nice", bob.City);                                       // the patched entity holds the stored row

        var late = EntityValues.Copy(read);
        late.City = "Lille";
        var conflict = await Assert.ThrowsAsync<EntityStoreException>(() => store.PatchAsync(read, late));   // City changed meanwhile
        Assert.Equal(EntityErrorCode.Conflict, conflict.ErrorCode);
    }

    [Fact]
    public async Task Many_rows_are_updated_and_deleted_by_the_server_in_one_call()
    {
        await SeedAsync();
        await using var store = Client();

        Assert.Equal(2, await store.UpdateWhereAsync<GInvoice>(i => i.Year == 2026, set => set.Set(i => i.Year, 2027)));
        Assert.Equal(2, _database.All<GInvoice>().Count(i => i.Year == 2027));

        Assert.Equal(1, await store.DeleteWhereAsync<GInvoiceLine>(l => l.Product == "cake"));
        Assert.False(_database.All<GInvoiceLine>().Any(l => l.Product == "cake"));
    }

    [Fact]
    public async Task Two_users_saving_different_lines_of_one_invoice_through_a_server_do_not_conflict()
    {
        await SeedAsync();
        var server = Server();
        var invoices = new GraphRepository<GInvoice, long>(ClientStores(server), GInvoiceGraph.Create());
        await using var reader = Client(server);
        var id = (await reader.SelectAsync<GInvoice>(i => i.Number == "A")).Single().Id;
        var ana = (await reader.SelectAsync<GInvoice>(i => i.Id == id)).Single();
        var bob = (await reader.SelectAsync<GInvoice>(i => i.Id == id)).Single();
        await invoices.LoadChildrenAsync(ana);
        await invoices.LoadChildrenAsync(bob);

        ana.Lines!.Single(l => l.Product == "tea").Quantity = 10;
        Assert.True((await invoices.SaveAsync(ana)).Successful);
        bob.Lines!.Single(l => l.Product == "cake").Quantity = 7;
        var saved = await invoices.SaveAsync(bob);

        Assert.True(saved.Successful, saved.Exception);
        var lines = _database.All<GInvoiceLine>().Where(l => l.GInvoiceId == id).ToList();
        Assert.Equal((10, 7), (lines.Single(l => l.Product == "tea").Quantity, lines.Single(l => l.Product == "cake").Quantity));
    }

    [Fact]
    public async Task Pages_after_a_row_go_as_deep_as_wanted()
    {
        await using (var seed = new MemoryStore(_database))
        {
            foreach (var name in new[] { "d", "a", "c", "b", "e", "b" })
                await seed.InsertAsync(new GClient { Name = name, City = "x" });
        }
        await using var store = Client();
        var sort = Sort<GClient>.By(c => c.Name);

        var seen = new List<string>();
        GClient? last = null;
        while (true)
        {
            var page = await store.SelectAfterAsync(c => c.City == "x", sort, last, 2);
            if (page.Count == 0)
                break;
            seen.AddRange(page.Select(c => c.Name));
            last = page[^1];
        }
        Assert.Equal(["a", "b", "b", "c", "d", "e"], seen);
    }
}

/// <summary>The requests reach the server and succeed there: no store falls back to reading rows.</summary>
public sealed class RemoteRequestsTests
{
    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();

    private sealed class Recording(IEntityTransport inner) : IEntityTransport
    {
        public List<(RemoteRequestKind Kind, bool Successful)> Requests { get; } = [];
        public Task<TransportResult> GetAllAsync(string type, CancellationToken ct = default) => inner.GetAllAsync(type, ct);
        public Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, bool any, CancellationToken ct = default) => inner.SelectAsync(type, conditions, any, ct);
        public Task<TransportResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize, CancellationToken ct = default)
            => inner.SelectPagedAsync(type, conditions, any, pageNumber, pageSize, ct);
        public Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default) => inner.GetChangesAsync(type, sinceUtc, ct);
        public Task<TransportResult> InsertAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default) => inner.InsertAsync(type, entity, connectionId, ct);
        public Task<TransportResult> UpdateAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default) => inner.UpdateAsync(type, entity, connectionId, ct);
        public Task<TransportResult> DeleteAsync(string type, byte[] entity, string? connectionId, CancellationToken ct = default) => inner.DeleteAsync(type, entity, connectionId, ct);
        public Task<TransportResult> ExecuteBatchAsync(BatchOperation[] operations, CancellationToken ct = default) => inner.ExecuteBatchAsync(operations, ct);

        public async Task<TransportResult> ExecuteAsync(string type, RemoteRequest request, CancellationToken ct = default)
        {
            var result = await inner.ExecuteAsync(type, request, ct);
            Requests.Add((request.Kind, result.Successful));
            return result;
        }
    }

    [Fact]
    public async Task Each_request_is_done_by_the_server()
    {
        var stores = new EntityStoreFactory(() => new MemoryStore(_database));
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<GClient>(stores, EntityAccessPolicies.AllowAll));
        var transport = new Recording(new EntityRequestDispatcher(registry, _serializer, stores).AsTransport());
        await using var store = new RemoteEntityStore(transport, _serializer);
        await store.InsertAsync(new GClient { Name = "Ana", City = "Paris" });
        var read = (await store.GetAllAsync<GClient>()).Single();
        var changed = EntityValues.Copy(read);
        changed.City = "Nice";

        await store.CountAsync<GClient>(c => c.City != "");
        await store.GroupAsync<GClient, string>(c => true, c => c.City);
        await store.SelectAsync<GClient, string>(c => true, c => c.Name);
        await store.PatchAsync(read, changed);
        await store.UpdateWhereAsync<GClient>(c => c.City == "Nice", set => set.Set(c => c.Name, "Anna"));
        await store.DeleteWhereAsync<GClient>(c => c.Name == "Anna");

        Assert.Equal(
            [RemoteRequestKind.Count, RemoteRequestKind.Aggregate, RemoteRequestKind.Project, RemoteRequestKind.Patch,
             RemoteRequestKind.UpdateWhere, RemoteRequestKind.DeleteWhere],
            transport.Requests.Select(r => r.Kind));
        Assert.All(transport.Requests, r => Assert.True(r.Successful, r.Kind.ToString()));
        Assert.Empty(_database.All<GClient>());
    }
}

/// <summary>A row created offline is referred to by its real key once the server gave it, by rows of other types too.</summary>
public sealed class OfflineReferencesTests
{
    private readonly MemoryDatabase _server = new();
    private readonly JsonTestSerializer _serializer = new();

    [Fact]
    public async Task A_child_created_offline_on_its_own_for_a_parent_created_offline_gets_its_real_key()
    {
        var serverStores = new EntityStoreFactory(() => new MemoryStore(_server).WithSyncConventions());
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<AuditPerson>(serverStores, EntityAccessPolicies.AllowAll));
        registry.Register(new EntityHandler<AuditContact>(serverStores, EntityAccessPolicies.AllowAll));
        var remote = new EntityRequestDispatcher(registry, _serializer, serverStores).AsTransport();
        EntityRelations.Register<AuditPerson>(EntityGraph.For<AuditPerson, long>(p => p.Id)
            .HasMany(p => p.Contacts, foreignKey: c => c.PersonId, key: c => c.Id));
        await using (var seed = new MemoryStore(_server).WithSyncConventions())
            await seed.InsertAsync(new AuditPerson { Name = "existing" });           // the server's keys differ from the temporary ones

        var mode = new OfflineMode(remote, _serializer, new MemoryOfflineStorage())
            .Add<AuditPerson, long>(p => p.Id)
            .Add<AuditContact, long>(c => c.Id);
        var stores = new OfflineStoreFactory(RemoteEntityStore.Factory(remote, _serializer), mode);
        Assert.True((await mode.GoOfflineAsync()).Successful);

        await using (var store = stores.Create())
        {
            var person = new AuditPerson { Name = "Ana" };
            await store.InsertAsync(person);
            Assert.True(person.Id < 0);
            await store.InsertAsync(new AuditContact { PersonId = person.Id, Label = "mobile" });
        }

        Assert.True((await mode.GoOnlineAsync()).Completed);
        var ana = Assert.Single(_server.All<AuditPerson>(), p => p.Name == "Ana");
        Assert.Equal(ana.Id, Assert.Single(_server.All<AuditContact>()).PersonId);
    }
}

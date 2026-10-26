using Grpc.Core;
using MagicOnion;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Queryeables.Abstraction;
using Queryeables.Dommel;
using Queryeables.EntityFramework;
using Queryeables.Onion;

namespace Queryeables.Tests.Contract;

/// <summary>In-memory SQLite database, private to each test, kept while a connection stays open.</summary>
public abstract class SqliteContractTests : EntityStoreContractTests
{
    protected string ConnectionString { get; } = $"Data Source=contract_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private SqliteConnection? _keepAlive;

    public override async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnectionString);
        await _keepAlive.OpenAsync();

        await using var command = _keepAlive.CreateCommand();
        command.CommandText = """
            CREATE TABLE Clients (
                Id      INTEGER PRIMARY KEY AUTOINCREMENT,
                Nom     TEXT    NOT NULL,
                Actif   INTEGER NOT NULL,
                Parrain INTEGER NULL,
                Version INTEGER NOT NULL DEFAULT 0,
                LastModified TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
                Deleted INTEGER NOT NULL DEFAULT 0
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    public override async Task DisposeAsync()
    {
        if (_keepAlive is not null)
            await _keepAlive.DisposeAsync();
    }

    protected IEntityStore CreateDommelStore() => new DommelEntityStore(new SqliteConnection(ConnectionString));
}

public sealed class DommelEntityStoreContractTests : SqliteContractTests
{
    protected override IEntityStore CreateStore() => CreateDommelStore();
}

/// <summary>Same contract, through <see cref="SyncConventionsStore"/>: timestamps and soft delete on top.</summary>
public sealed class DommelWithSyncConventionsContractTests : SqliteContractTests
{
    protected override bool UsesSyncConventions => true;
    protected override IEntityStore CreateStore() => CreateDommelStore().WithSyncConventions();
}

public sealed class EfEntityStoreContractTests : SqliteContractTests
{
    protected override IEntityStore CreateStore()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(ConnectionString).Options;
        return new EfEntityStore(new TestDbContext(options));
    }

    internal sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<TestClient>();
    }
}

public sealed class EfWithSyncConventionsContractTests : SqliteContractTests
{
    protected override bool UsesSyncConventions => true;

    protected override IEntityStore CreateStore()
    {
        var options = new DbContextOptionsBuilder<EfEntityStoreContractTests.TestDbContext>().UseSqlite(ConnectionString).Options;
        return new EfEntityStore(new EfEntityStoreContractTests.TestDbContext(options)).WithSyncConventions();
    }
}

/// <summary>
/// Remote store: <see cref="MagicOnionEntityStore"/> → MessagePack serialization → <see cref="EntityRequestDispatcher"/>
/// → <see cref="EntityHandler{T}"/> → Dommel. Only the network is replaced by a direct call.
/// </summary>
public sealed class MagicOnionEntityStoreContractTests : SqliteContractTests
{
    protected override IEntityStore CreateStore() => CreateRemoteStore(EntityAccessPolicies.AllowAll);

    private IEntityStore CreateRemoteStore(IEntityAccessPolicy policy, bool syncConventions = false, int maxPageSize = 1000)
    {
        Func<IEntityStore> serverStore = syncConventions
            ? () => CreateDommelStore().WithSyncConventions()
            : CreateDommelStore;

        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<TestClient>(serverStore, policy, maxPageSize: maxPageSize));
        var dispatcher = new EntityRequestDispatcher(registry, transactionStoreFactory: serverStore);
        return new MagicOnionEntityStore(new InProcessLayer(dispatcher));
    }

    [Fact]
    public async Task Server_caps_the_page_size()
    {
        await using var store = CreateRemoteStore(EntityAccessPolicies.AllowAll, maxPageSize: 2);
        for (var i = 0; i < 5; i++)
            await store.InsertAsync(new TestClient { Nom = $"C{i}" });

        Assert.Equal(2, (await store.SelectPagedAsync<TestClient>(x => true, 1, 5000)).Count);
    }

    [Fact]
    public async Task Remote_changes_include_soft_deleted_rows()
    {
        await using var store = CreateRemoteStore(EntityAccessPolicies.AllowAll, syncConventions: true);
        var a = new TestClient { Nom = "Ana" };
        await store.InsertAsync(a);
        await store.InsertAsync(new TestClient { Nom = "Bob" });

        Assert.True(await store.DeleteAsync(a));

        Assert.Equal("Bob", Assert.Single(await store.GetAllAsync<TestClient>()).Nom);
        var changes = await store.SelectChangesAsync<TestClient>(DateTime.MinValue);
        Assert.True(Assert.Single(changes, c => c.Nom == "Ana").Deleted);
    }

    [Fact]
    public async Task Commit_sends_one_batch_and_assigns_generated_keys()
    {
        var a = new TestClient { Nom = "Ana" };
        var b = new TestClient { Nom = "Bob" };

        await using var store = CreateStore();
        await store.RunInTransactionAsync(async s =>
        {
            await s.InsertAsync(a);
            await s.InsertAsync(b);
            Assert.Equal(0, a.Id); // nothing is sent before Commit
        });

        Assert.True(a.Id > 0);
        Assert.True(b.Id > a.Id);
    }

    [Fact]
    public async Task A_failing_operation_cancels_the_whole_batch()
    {
        await using var store = CreateStore();

        var error = await Assert.ThrowsAsync<EntityStoreException>(() => store.RunInTransactionAsync(async s =>
        {
            await s.InsertAsync(new TestClient { Nom = "Ana" });
            await s.UpdateAsync(new TestClient { Id = 999, Nom = "Fantôme" }); // nonexistent row
        }));

        Assert.Equal(EntityErrorCode.NotFound, error.ErrorCode);
        Assert.Empty(await store.GetAllAsync<TestClient>()); // the insert was rolled back too
    }

    [Fact]
    public async Task Access_policy_is_enforced()
    {
        await using (var writer = CreateStore())
            await writer.InsertAsync(new TestClient { Nom = "Ana" });

        await using var readOnly = CreateRemoteStore(EntityAccessPolicies.From(r => r.Operation == EntityOperation.Read));

        Assert.Single(await readOnly.GetAllAsync<TestClient>());
        var error = await Assert.ThrowsAsync<EntityStoreException>(() => readOnly.InsertAsync(new TestClient { Nom = "Bob" }));
        Assert.Equal(EntityErrorCode.Forbidden, error.ErrorCode);
    }

    [Fact]
    public async Task Invalid_entities_are_rejected_by_the_server()
    {
        await using var store = CreateStore();

        var error = await Assert.ThrowsAsync<EntityStoreException>(() => store.InsertAsync(new TestClient { Nom = "" }));

        Assert.Equal(EntityErrorCode.Validation, error.ErrorCode);
        Assert.Contains("Le nom est obligatoire.", error.Errors);
        Assert.Empty(await store.GetAllAsync<TestClient>());
    }

    [Fact]
    public async Task Unsupported_predicates_fail_before_calling_the_server()
    {
        await using var store = CreateStore();
        await Assert.ThrowsAsync<NotSupportedException>(() => store.SelectAsync<TestClient>(x => x.Nom.Length > 3));
    }

    /// <summary>The MagicOnion service without the network: each call goes straight to the dispatcher.</summary>
    private sealed class InProcessLayer(EntityRequestDispatcher dispatcher) : IMagicOnionLayer
    {
        private static readonly EntityRequestContext Caller = EntityRequestContext.Anonymous;

        public UnaryResult<DBResult> GetAllAsync(string type) => new(dispatcher.GetAllAsync(type, Caller));
        public UnaryResult<DBResult> SelectAsync(string type, FilterCondition[] conditions, bool any)
            => new(dispatcher.SelectAsync(type, RoundTrip(conditions), any, Caller));
        public UnaryResult<DBResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize)
            => new(dispatcher.SelectPagedAsync(type, RoundTrip(conditions), any, pageNumber, pageSize, Caller));
        public UnaryResult<DBResult> GetChangesAsync(string type, DateTime sinceUtc)
            => new(dispatcher.GetChangesAsync(type, RoundTrip(sinceUtc), Caller));
        public UnaryResult<DBResult> InsertAsync(string type, byte[] entity, string? connectionId)
            => new(dispatcher.InsertAsync(type, entity, Caller.WithConnection(connectionId)));
        public UnaryResult<DBResult> UpdateAsync(string type, byte[] entity, string? connectionId)
            => new(dispatcher.UpdateAsync(type, entity, Caller.WithConnection(connectionId)));
        public UnaryResult<DBResult> DeleteAsync(string type, byte[] entity, string? connectionId)
            => new(dispatcher.DeleteAsync(type, entity, Caller.WithConnection(connectionId)));
        public UnaryResult<DBResult> ExecuteBatchAsync(BatchOperation[] operations)
            => new(dispatcher.ExecuteBatchAsync(RoundTrip(operations), Caller));

        // Arguments go through MessagePack as on the network: numbers come back with the smallest possible type.
        private static T RoundTrip<T>(T value)
            => MessagePack.MessagePackSerializer.Deserialize<T>(MessagePack.MessagePackSerializer.Serialize(value));

        public IMagicOnionLayer WithOptions(CallOptions option) => this;
        public IMagicOnionLayer WithHeaders(Metadata headers) => this;
        public IMagicOnionLayer WithDeadline(DateTime deadline) => this;
        public IMagicOnionLayer WithCancellationToken(CancellationToken cancellationToken) => this;
        public IMagicOnionLayer WithHost(string host) => this;
    }
}

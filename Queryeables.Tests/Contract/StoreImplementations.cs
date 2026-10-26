using Grpc.Core;
using MagicOnion;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Queryeables.Abstraction;
using Queryeables.Dommel;
using Queryeables.EntityFramework;
using Queryeables.Onion;

namespace Queryeables.Tests.Contract;

/// <summary>Base SQLite en mémoire, propre à chaque test, conservée tant qu'une connexion reste ouverte.</summary>
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
                Version INTEGER NOT NULL DEFAULT 0
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

public sealed class EfEntityStoreContractTests : SqliteContractTests
{
    protected override IEntityStore CreateStore()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(ConnectionString).Options;
        return new EfEntityStore(new TestDbContext(options));
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<TestClient>();
    }
}

/// <summary>
/// Store distant : <see cref="MagicOnionEntityStore"/> → sérialisation MessagePack → <see cref="EntityRequestDispatcher"/>
/// → <see cref="EntityHandler{T}"/> → Dommel. Seul le réseau est remplacé par un appel direct.
/// </summary>
public sealed class MagicOnionEntityStoreContractTests : SqliteContractTests
{
    protected override IEntityStore CreateStore() => CreateRemoteStore(EntityAccessPolicies.AllowAll);

    private IEntityStore CreateRemoteStore(IEntityAccessPolicy policy)
    {
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<TestClient>(CreateDommelStore, policy));
        var dispatcher = new EntityRequestDispatcher(registry, transactionStoreFactory: CreateDommelStore);
        return new MagicOnionEntityStore(new InProcessLayer(dispatcher));
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
            Assert.Equal(0, a.Id); // rien n'est envoyé avant le Commit
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
            await s.UpdateAsync(new TestClient { Id = 999, Nom = "Fantôme" }); // ligne inexistante
        }));

        Assert.Equal(EntityErrorCode.NotFound, error.ErrorCode);
        Assert.Empty(await store.GetAllAsync<TestClient>()); // l'insertion a été annulée aussi
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

    /// <summary>Le service MagicOnion sans le réseau : chaque appel va directement au dispatcher.</summary>
    private sealed class InProcessLayer(EntityRequestDispatcher dispatcher) : IMagicOnionLayer
    {
        private static readonly EntityRequestContext Caller = EntityRequestContext.Anonymous;

        public UnaryResult<DBResult> GetAllAsync(string type) => new(dispatcher.GetAllAsync(type, Caller));
        public UnaryResult<DBResult> SelectAsync(string type, FilterCondition[] conditions, bool any)
            => new(dispatcher.SelectAsync(type, RoundTrip(conditions), any, Caller));
        public UnaryResult<DBResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize)
            => new(dispatcher.SelectPagedAsync(type, RoundTrip(conditions), any, pageNumber, pageSize, Caller));
        public UnaryResult<DBResult> InsertAsync(string type, byte[] entity, string? connectionId)
            => new(dispatcher.InsertAsync(type, entity, Caller.WithConnection(connectionId)));
        public UnaryResult<DBResult> UpdateAsync(string type, byte[] entity, string? connectionId)
            => new(dispatcher.UpdateAsync(type, entity, Caller.WithConnection(connectionId)));
        public UnaryResult<DBResult> DeleteAsync(string type, byte[] entity, string? connectionId)
            => new(dispatcher.DeleteAsync(type, entity, Caller.WithConnection(connectionId)));
        public UnaryResult<DBResult> ExecuteBatchAsync(BatchOperation[] operations)
            => new(dispatcher.ExecuteBatchAsync(RoundTrip(operations), Caller));

        // Les arguments passent par MessagePack comme sur le réseau : les nombres reviennent avec le plus petit type possible.
        private static T RoundTrip<T>(T value)
            => MessagePack.MessagePackSerializer.Deserialize<T>(MessagePack.MessagePackSerializer.Serialize(value));

        public IMagicOnionLayer WithOptions(CallOptions option) => this;
        public IMagicOnionLayer WithHeaders(Metadata headers) => this;
        public IMagicOnionLayer WithDeadline(DateTime deadline) => this;
        public IMagicOnionLayer WithCancellationToken(CancellationToken cancellationToken) => this;
        public IMagicOnionLayer WithHost(string host) => this;
    }
}

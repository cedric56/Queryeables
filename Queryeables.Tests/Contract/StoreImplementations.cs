using Grpc.Core;
using MagicOnion;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Queryeables.Abstraction;
using Queryeables.Dommel;
using Queryeables.EntityFramework;
using Queryeables.MagicOnion;
using Queryeables.Remote;

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
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT    NOT NULL,
                Active    INTEGER NOT NULL,
                SponsorId INTEGER NULL,
                Version   INTEGER NOT NULL DEFAULT 0,
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

/// <summary>Same contract, through <see cref="SyncConventionsDecorator"/>: timestamps and soft delete on top.</summary>
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
/// A replica over MagicOnion: <see cref="RemoteEntityStore"/> → <see cref="MagicOnionTransport"/> → MessagePack messages →
/// <see cref="EntityRequestDispatcher"/> → <see cref="EntityHandler{T}"/> → Dommel. Only the network is replaced by a direct call.
/// </summary>
public sealed class MagicOnionReplicaContractTests : IAsyncLifetime
{
    private readonly string _connectionString = $"Data Source=replica_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection? _keepAlive;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();
        await using var command = _keepAlive.CreateCommand();
        command.CommandText = """
            CREATE TABLE Clients (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT    NOT NULL,
                Active    INTEGER NOT NULL,
                SponsorId INTEGER NULL,
                Version   INTEGER NOT NULL DEFAULT 0,
                LastModified TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
                Deleted INTEGER NOT NULL DEFAULT 0
            );
            INSERT INTO Clients (Name, Active) VALUES ('Ana', 1), ('Bob', 0);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_keepAlive is not null)
            await _keepAlive.DisposeAsync();
    }

    private IEntityStore Replica(RowLevelSecurity<TestClient>? rows = null)
    {
        IEntityStore Server() => new DommelEntityStore(new SqliteConnection(_connectionString));
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<TestClient>(Server, EntityAccessPolicies.AllowAll, rows));
        var dispatcher = new EntityRequestDispatcher(registry, MessagePackEntitySerializer.Default);
        return MagicOnionTransport.Stores(new InProcessLayer(dispatcher), MessagePackEntitySerializer.Default).Create();
    }

    [Fact]
    public async Task A_replica_reads_the_rows_of_its_user_and_a_row_by_key_over_MagicOnion()
    {
        await using var store = Replica(RowLevelSecurity.For<TestClient, long>(c => c.Id, _ => c => c.Active));

        var ana = Assert.Single(await store.GetAllAsync<TestClient>());
        var id = ana.Id;

        Assert.Equal("Ana", ana.Name);
        Assert.Equal("Ana", (await store.FirstOrDefaultAsync<TestClient>(c => c.Id == id))?.Name);
    }

    [Fact]
    public async Task A_replica_refuses_writes_and_queries_over_MagicOnion()
    {
        await using var store = Replica();

        var write = await Assert.ThrowsAsync<EntityStoreException>(() => store.InsertAsync(new TestClient { Name = "Eve" }));
        var query = await Assert.ThrowsAsync<EntityStoreException>(() => store.SelectAsync<TestClient>(c => c.Name == "Ana"));

        Assert.Equal(EntityErrorCode.NotSupported, write.ErrorCode);
        Assert.Equal(EntityErrorCode.NotSupported, query.ErrorCode);
    }

    private sealed class InProcessLayer(EntityRequestDispatcher dispatcher) : IMagicOnionLayer
    {
        private static readonly EntityRequestContext Caller = EntityRequestContext.Anonymous;

        public UnaryResult<EntityResultMessage> GetAllAsync(string type) => Reply(dispatcher.GetAllAsync(type, Caller));
        public UnaryResult<EntityResultMessage> SelectAsync(string type, FilterConditionMessage[] conditions)
            => Reply(dispatcher.SelectAsync(type, FilterConditionMessage.ToConditions(RoundTrip(conditions)), Caller));
        public UnaryResult<EntityResultMessage> GetChangesAsync(string type, DateTime sinceUtc)
            => Reply(dispatcher.GetChangesAsync(type, RoundTrip(sinceUtc), Caller));

        private static UnaryResult<EntityResultMessage> Reply(Task<TransportResult> call) => new(ReplyAsync(call));

        private static async Task<EntityResultMessage> ReplyAsync(Task<TransportResult> call) => RoundTrip(EntityResultMessage.From(await call));

        private static T RoundTrip<T>(T value)
            => MessagePack.MessagePackSerializer.Deserialize<T>(MessagePack.MessagePackSerializer.Serialize(value));

        public IMagicOnionLayer WithOptions(CallOptions option) => this;
        public IMagicOnionLayer WithHeaders(Metadata headers) => this;
        public IMagicOnionLayer WithDeadline(DateTime deadline) => this;
        public IMagicOnionLayer WithCancellationToken(CancellationToken cancellationToken) => this;
        public IMagicOnionLayer WithHost(string host) => this;
    }
}

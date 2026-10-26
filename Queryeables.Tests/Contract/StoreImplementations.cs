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
                Parrain INTEGER NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    public override async Task DisposeAsync()
    {
        if (_keepAlive is not null)
            await _keepAlive.DisposeAsync();
    }
}

public sealed class DommelEntityStoreContractTests : SqliteContractTests
{
    protected override IEntityStore CreateStore() => new DommelEntityStore(new SqliteConnection(ConnectionString));
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
    protected override bool SupportsTransactions => false;

    protected override IEntityStore CreateStore()
    {
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<TestClient>(() => new DommelEntityStore(new SqliteConnection(ConnectionString))));
        return new MagicOnionEntityStore(new InProcessLayer(new EntityRequestDispatcher(registry)));
    }

    [Fact]
    public async Task Transactions_are_not_supported()
    {
        await using var store = CreateStore();
        await Assert.ThrowsAsync<NotSupportedException>(() => store.BeginTransactionAsync());
    }

    /// <summary>Le service MagicOnion sans le réseau : chaque appel va directement au dispatcher.</summary>
    private sealed class InProcessLayer(EntityRequestDispatcher dispatcher) : IMagicOnionLayer
    {
        public UnaryResult<DBResult> GetAllAsync(string type) => new(dispatcher.GetAllAsync(type));
        public UnaryResult<DBResult> SelectAsync(string type, Dictionary<string, object?> filters, bool any)
            => new(dispatcher.SelectAsync(type, RoundTrip(filters), any));
        public UnaryResult<DBResult> SelectPagedAsync(string type, Dictionary<string, object?> filters, bool any, int pageNumber, int pageSize)
            => new(dispatcher.SelectPagedAsync(type, RoundTrip(filters), any, pageNumber, pageSize));
        public UnaryResult<DBResult> InsertAsync(string type, byte[] entity, string? connectionId) => new(dispatcher.InsertAsync(type, entity, connectionId));
        public UnaryResult<DBResult> UpdateAsync(string type, byte[] entity, string? connectionId) => new(dispatcher.UpdateAsync(type, entity, connectionId));
        public UnaryResult<DBResult> DeleteAsync(string type, byte[] entity, string? connectionId) => new(dispatcher.DeleteAsync(type, entity, connectionId));

        // Les filtres passent par MessagePack comme sur le réseau : les nombres reviennent avec le plus petit type possible.
        private static Dictionary<string, object?> RoundTrip(Dictionary<string, object?> filters)
            => MessagePack.MessagePackSerializer.Deserialize<Dictionary<string, object?>>(
                MessagePack.MessagePackSerializer.Serialize(filters));

        public IMagicOnionLayer WithOptions(CallOptions option) => this;
        public IMagicOnionLayer WithHeaders(Metadata headers) => this;
        public IMagicOnionLayer WithDeadline(DateTime deadline) => this;
        public IMagicOnionLayer WithCancellationToken(CancellationToken cancellationToken) => this;
        public IMagicOnionLayer WithHost(string host) => this;
    }
}

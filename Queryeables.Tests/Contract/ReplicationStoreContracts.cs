using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using Queryeables.Abstraction;
using Queryeables.EntityFramework;
using Queryeables.MongoDB;
using Queryeables.Replication;

namespace Queryeables.Tests.Contract;

/// <summary>The replication store on EF Core and SQLite: an in-memory database private to each test.</summary>
public sealed class EfReplicationStoreContractTests : ReplicationStoreContractTests
{
    private readonly string _connectionString = $"Data Source=replication_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection? _keepAlive;

    private ReplicationDbContext Context() => new(new DbContextOptionsBuilder<ReplicationDbContext>().UseSqlite(_connectionString).Options);

    public override async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();
        await using var context = Context();
        await context.Database.EnsureCreatedAsync();
    }

    public override async Task DisposeAsync()
    {
        if (_keepAlive is not null)
            await _keepAlive.DisposeAsync();
    }

    protected override IEntityStoreFactory Stores => new EntityStoreFactory(() => new EfEntityStore(Context()));

    /// <summary>The four tables of the replication store, as an application maps them, and the projected entity.</summary>
    internal sealed class ReplicationDbContext(DbContextOptions<ReplicationDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ReplicationLogRow>().HasIndex(r => r.Position).IsUnique();
            modelBuilder.Entity<ReplicationDocumentRow>().HasIndex(r => new { r.Collection, r.Key }).IsUnique();
            modelBuilder.Entity<ReplicationReplicaRow>().HasIndex(r => r.Replica).IsUnique();
            modelBuilder.Entity<ReplicationHeadRow>();
            modelBuilder.Entity<ReplicatedPerson>().HasIndex(p => p.Key).IsUnique();
        }
    }
}

/// <summary>The replication store on MongoDB (a replica set, for the transactions), when QUERYEABLES_MONGODB is set.</summary>
[RequiresServer(MongoServer.Variable)]
public sealed class MongoReplicationStoreContractTests : ReplicationStoreContractTests
{
    private IMongoClient? _client;
    private string _name = string.Empty;
    private IMongoDatabase _database = null!;

    public override Task InitializeAsync()
    {
        _client = new MongoClient(MongoServer.ConnectionString);
        _name = $"replication_{Guid.NewGuid():N}";
        _database = _client.GetDatabase(_name);
        return Task.CompletedTask;
    }

    public override async Task DisposeAsync()
    {
        if (_client is not null)
            await _client.DropDatabaseAsync(_name);
    }

    protected override IEntityStoreFactory Stores => new EntityStoreFactory(() => new MongoEntityStore(_database));
}

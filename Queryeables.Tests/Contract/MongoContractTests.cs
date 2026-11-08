using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Queryeables.Abstraction;
using Queryeables.MongoDB;

namespace Queryeables.Tests.Contract;

/// <summary>
/// The store contract on MongoDB, when QUERYEABLES_MONGODB holds a connection string to a replica set (the CI starts a
/// single-node one); skipped otherwise. Each test uses a database of its own, dropped afterwards.
/// </summary>
[RequiresServer(MongoServer.Variable)]
public class MongoEntityStoreContractTests : EntityStoreContractTests
{
    private IMongoClient? _client;
    private string _name = string.Empty;

    protected IMongoDatabase Database { get; private set; } = null!;

    public override Task InitializeAsync()
    {
        _client = new MongoClient(MongoServer.ConnectionString);
        _name = $"contract_{Guid.NewGuid():N}";
        Database = _client.GetDatabase(_name);
        return Task.CompletedTask;
    }

    public override async Task DisposeAsync()
    {
        if (_client is not null)
            await _client.DropDatabaseAsync(_name);
    }

    protected override IEntityStore CreateStore() => new MongoEntityStore(Database);
}

[RequiresServer(MongoServer.Variable)]
public sealed class MongoWithSyncConventionsContractTests : MongoEntityStoreContractTests
{
    protected override bool UsesSyncConventions => true;

    protected override IEntityStore CreateStore() => base.CreateStore().WithSyncConventions();
}

internal static class MongoServer
{
    public const string Variable = "QUERYEABLES_MONGODB";

    public static string ConnectionString => Environment.GetEnvironmentVariable(Variable)
        ?? throw new InvalidOperationException($"{Variable} is not set.");
}

/// <summary>What a change stream reports, read without a server.</summary>
public class MongoChangeReadingTests
{
    [Fact]
    public void Document_changes_become_row_changes()
    {
        var key = new BsonDocument("_id", new BsonInt64(42));

        Assert.Equal((ChangeType.Insert, "Clients", "42"), MongoChangeListener.Read(ChangeStreamOperationType.Insert, "Clients", key));
        Assert.Equal((ChangeType.Update, "Clients", "42"), MongoChangeListener.Read(ChangeStreamOperationType.Replace, "Clients", key));
        Assert.Equal((ChangeType.Delete, "Clients", "42"), MongoChangeListener.Read(ChangeStreamOperationType.Delete, "Clients", key));
        Assert.Null(MongoChangeListener.Read(ChangeStreamOperationType.Drop, "Clients", null));
    }

    [Fact]
    public void Keys_are_written_as_the_caches_parse_them()
    {
        var guid = Guid.NewGuid();

        Assert.Equal(EntityKey.Format(7L), MongoChangeListener.KeyText(new BsonInt64(7)));
        Assert.Equal("abc", MongoChangeListener.KeyText(new BsonString("abc")));
        Assert.Equal(EntityKey.Format(guid), MongoChangeListener.KeyText(new BsonBinaryData(guid, GuidRepresentation.Standard)));
        Assert.Equal(guid, EntityKey.Parse<Guid>(MongoChangeListener.KeyText(new BsonBinaryData(guid, GuidRepresentation.Standard))));
    }

    [Fact]
    public void Collections_are_named_as_the_change_feeds_name_their_tables()
        => Assert.Equal(new ChangeFeed<TestClient>().Table, MongoCollections.NameOf(typeof(TestClient)));
}

/// <summary>A cache that follows a MongoDB collection through a change stream, against a real server.</summary>
[RequiresServer(MongoServer.Variable)]
public class MongoNotificationsTests
{
    [ServerFact]
    public async Task A_cache_follows_a_MongoDB_collection()
    {
        var database = $"notifications_{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.UseQueryeables(q => q
            .UseMongoDB(MongoServer.ConnectionString, database, notifications: n => n.Watch<TestClient>())
            .AddCache<TestClient, long>(c => c.Id));
        await using var provider = services.BuildServiceProvider();
        var listener = provider.GetRequiredService<MongoChangeListener>();
        await listener.StartAsync();
        try
        {
            await listener.Listening.WaitAsync(TimeSpan.FromSeconds(30));
            var cache = provider.GetRequiredService<IEntityCache<TestClient, long>>();
            await cache.LoadAsync();

            // Written by someone else, straight into the collection: the change stream reports it.
            var outside = new MongoClient(MongoServer.ConnectionString).GetDatabase(database).GetCollection<TestClient>("Clients");
            await outside.InsertOneAsync(new TestClient { Id = 1, Name = "Ana" });
            for (var i = 0; i < 300 && cache.Find(1) is null; i++)
                await Task.Delay(50);
            Assert.Equal("Ana", cache.Find(1)?.Name);

            await outside.DeleteOneAsync(c => c.Id == 1);
            for (var i = 0; i < 300 && cache.Find(1) is not null; i++)
                await Task.Delay(50);
            Assert.Null(cache.Find(1));
        }
        finally
        {
            await listener.StopAsync();
            await provider.GetRequiredService<IMongoClient>().DropDatabaseAsync(database);
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using Queryeables.Abstraction;
using Queryeables.PostgreSql;
using System.ComponentModel.DataAnnotations.Schema;

namespace Queryeables.Tests.Unit;

[Table("PgItem")]
public sealed class PgItem
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class PostgresNotificationsTests
{
    [Fact]
    public void A_notification_is_read()
    {
        Assert.Equal((ChangeType.Insert, "PgItem", "42"),
            PostgresChangeListener.Parse("""{"table":"PgItem","op":"INSERT","key":"42"}""")!.Value);
        Assert.Equal((ChangeType.Delete, "PgItem", ""),
            PostgresChangeListener.Parse("""{"table":"PgItem","op":"DELETE","key":null}""")!.Value);
        Assert.Null(PostgresChangeListener.Parse("""{"table":"PgItem","op":"TRUNCATE"}"""));
    }

    [Fact]
    public void Watched_tables_use_the_names_of_the_monitors()
    {
        var notifications = new PostgresNotifications().Watch<PgItem>(i => i.Id).Watch<TestClient>(c => c.Id);

        Assert.Equal(new[] { "PgItem", "Clients" }, notifications.Tables.Select(t => t.Name));
        Assert.Equal(new ChangeFeed<PgItem>().Table, notifications.Tables[0].Name);

        var script = notifications.TriggerScript();
        Assert.Contains("CREATE OR REPLACE FUNCTION queryeables_notify()", script);
        Assert.Contains("""ON "PgItem" FOR EACH ROW EXECUTE FUNCTION queryeables_notify('Id')""", script);
        Assert.Contains("""DROP TRIGGER IF EXISTS "queryeables_notify_Clients" ON "Clients";""", script);
    }

    [Fact]
    public async Task A_resync_makes_a_cache_synchronize()
    {
        var rows = new List<TestClient> { new() { Id = 1, Name = "Ana" } };
        var registry = new ChangeFeedRegistry();
        var monitor = new ChangeFeed<TestClient>();
        registry.Add(monitor);
        using var cache = new EntityCache<TestClient, long>(() => new InMemoryEntityStore(rows), c => c.Id, monitor, EntityKey.Parse<long>);
        await cache.LoadAsync();

        rows.Add(new TestClient { Id = 2, Name = "Bob" });      // missed while disconnected
        var changed = new TaskCompletionSource();
        cache.EntitiesChanged += (_, _) => changed.TrySetResult();
        registry.PublishResync();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Bob", cache.Find(2)?.Name);
    }

    /// <summary>
    /// Against a real PostgreSQL server, when QUERYEABLES_POSTGRES holds its connection string (the CI starts one); passes
    /// without doing anything otherwise.
    /// </summary>
    [Fact]
    public async Task A_cache_follows_a_PostgreSQL_table()
    {
        var connectionString = Environment.GetEnvironmentVariable("QUERYEABLES_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using (var setup = new Npgsql.NpgsqlConnection(connectionString))
        {
            await setup.OpenAsync();
            await using var create = new Npgsql.NpgsqlCommand(
                """DROP TABLE IF EXISTS "PgItem"; CREATE TABLE "PgItem" ("Id" bigserial PRIMARY KEY, "Name" text NOT NULL);""", setup);
            await create.ExecuteNonQueryAsync();
        }

        var services = new ServiceCollection();
        services.UseQueryeables(q => q
            .UseNpgsql(connectionString, notifications: n => n.Watch<PgItem>(i => i.Id))
            .AddCache<PgItem, long>(i => i.Id));
        await using var provider = services.BuildServiceProvider();
        var listener = provider.GetRequiredService<PostgresChangeListener>();
        await listener.StartAsync();
        await listener.Listening.WaitAsync(TimeSpan.FromSeconds(30));

        var cache = provider.GetRequiredService<IEntityCache<PgItem, long>>();
        await cache.LoadAsync();
        var stores = provider.GetRequiredService<IEntityStoreFactory>();

        async Task ChangeAndWaitAsync(Func<IEntityStore, Task> change, Func<bool> done)
        {
            var changed = new TaskCompletionSource();
            void OnChanged(object? sender, EventArgs e) { if (done()) changed.TrySetResult(); }
            cache.EntitiesChanged += OnChanged;
            try
            {
                await using (var store = stores.Create())
                    await change(store);
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                cache.EntitiesChanged -= OnChanged;
            }
        }

        var item = new PgItem { Name = "Ana" };
        await ChangeAndWaitAsync(s => s.InsertAsync(item), () => cache.Find(item.Id)?.Name == "Ana");

        item.Name = "Ana B";
        await ChangeAndWaitAsync(s => s.UpdateAsync(item), () => cache.Find(item.Id)?.Name == "Ana B");

        await ChangeAndWaitAsync(s => s.DeleteAsync(item), () => cache.Find(item.Id) is null);

        // A rolled back insert is never reported.
        await using (var store = stores.Create())
        {
            await using var transaction = await store.BeginTransactionAsync();
            await store.InsertAsync(new PgItem { Name = "Ghost" });
            await transaction.RollbackAsync();
        }
        await Task.Delay(500);
        Assert.DoesNotContain("Ghost", cache.Entities.Select(e => e.Name));

        await listener.StopAsync();
    }
}

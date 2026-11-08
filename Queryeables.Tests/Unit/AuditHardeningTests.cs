using Queryeables.Abstraction;
using Queryeables.Remote.Offline;
using System.Security.Cryptography;
using System.Text;

namespace Queryeables.Tests.Unit;

/// <summary>The offline copy encrypted at rest, and a cache that says how fresh it is.</summary>
public class AuditHardeningTests
{
    // ---------- Encrypted offline storage ----------

    private sealed class SpyStorage : IOfflineStorage
    {
        public Dictionary<string, byte[]> Values { get; } = [];

        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken ct = default)
            => ValueTask.FromResult(Values.TryGetValue(key, out var value) ? value : null);

        public ValueTask WriteAsync(string key, byte[] value, CancellationToken ct = default)
        {
            Values[key] = value;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string key, CancellationToken ct = default)
        {
            Values.Remove(key);
            return ValueTask.CompletedTask;
        }
    }

    private static bool Contains(byte[] haystack, string text) => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    [Fact]
    public async Task Neither_the_names_nor_the_values_can_be_read_in_the_inner_storage()
    {
        var inner = new SpyStorage();
        var storage = new EncryptedOfflineStorage(inner, EncryptedOfflineStorage.NewKey());

        await storage.WriteAsync("app/Patient/row/42", Encoding.UTF8.GetBytes("{\"Name\":\"Ana Secret\"}"));

        var (name, value) = Assert.Single(inner.Values);
        Assert.Matches("^[0-9a-f]{64}$", name);              // a hash: neither the type nor the row key
        Assert.DoesNotContain("Patient", name);
        Assert.False(Contains(value, "Ana Secret"));
        Assert.Equal("{\"Name\":\"Ana Secret\"}", Encoding.UTF8.GetString((await storage.ReadAsync("app/Patient/row/42"))!));
    }

    [Fact]
    public async Task A_value_moved_to_another_name_or_read_with_another_key_is_refused_not_dropped()
    {
        var inner = new SpyStorage();
        var key = EncryptedOfflineStorage.NewKey();
        var storage = new EncryptedOfflineStorage(inner, key);
        await storage.WriteAsync("app/row/1", [1]);
        var one = Assert.Single(inner.Values.Keys);
        await storage.WriteAsync("app/row/2", [2]);
        var two = inner.Values.Keys.Single(n => n != one);

        // Row 1's value copied over row 2's: authenticated with its name, it does not pass as row 2.
        inner.Values[two] = inner.Values[one].ToArray();
        await Assert.ThrowsAnyAsync<CryptographicException>(() => storage.ReadAsync("app/row/2").AsTask());

        var other = new EncryptedOfflineStorage(inner, EncryptedOfflineStorage.NewKey());
        Assert.Null(await other.ReadAsync("app/row/1"));                   // another key: other names, nothing found
        inner.Values[one][^1] ^= 1;                                  // altered
        await Assert.ThrowsAnyAsync<CryptographicException>(() => storage.ReadAsync("app/row/1").AsTask());
    }

    [Fact]
    public void The_key_must_be_256_bits()
        => Assert.Throws<ArgumentException>(() => new EncryptedOfflineStorage(new SpyStorage(), new byte[16]));

    // ---------- Cache freshness ----------

    private static bool _down;

    private static IEntityStore Flaky(List<TestClient> rows)
        => _down ? throw new HttpRequestException("No network.") : new InMemoryEntityStore(rows);

    [Fact]
    public async Task A_cache_says_when_its_last_read_failed_and_when_it_last_succeeded()
    {
        var rows = new List<TestClient> { new() { Id = 1, Name = "Ana" } };
        using var cache = new EntityCache<TestClient, long>(() => Flaky(rows), c => c.Id);
        Assert.Equal(CacheFreshness.NotLoaded, cache.Freshness);

        _down = false;
        await cache.LoadAsync();
        Assert.Equal(CacheFreshness.Current, cache.Freshness);
        var loadedAt = cache.LastRefreshedAt;
        Assert.NotNull(loadedAt);

        _down = true;
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => cache.SyncAsync());
            Assert.Equal(CacheFreshness.Stale, cache.Freshness);
            Assert.IsType<HttpRequestException>(cache.LastError);
            Assert.Equal(loadedAt, cache.LastRefreshedAt);
            Assert.Equal("Ana", cache.Find(1)?.Name);                         // the previous content is kept
        }
        finally
        {
            _down = false;
        }

        await cache.SyncAsync();
        Assert.Equal(CacheFreshness.Current, cache.Freshness);
        Assert.Null(cache.LastError);

        await cache.ClearUserDataAsync();
        Assert.Equal(CacheFreshness.NotLoaded, cache.Freshness);
        Assert.Null(cache.LastRefreshedAt);
    }
}

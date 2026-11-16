using Queryeables.Abstraction;
using System.Security.Cryptography;
using System.Text;

namespace Queryeables.Tests.Unit;

/// <summary>The offline copy encrypted at rest, and a cache that says how fresh it is.</summary>
public class AuditHardeningTests
{
    // ---------- Encrypted offline storage ----------

    private static bool Contains(byte[] haystack, string text) => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

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

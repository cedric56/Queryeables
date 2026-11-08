using Queryeables.Remote.Offline;
using Queryeables.Sqlite;

namespace Queryeables.Tests.Contract;

/// <summary>What every <see cref="IOfflineStorage"/> must do: the offline mode relies on nothing else.</summary>
public abstract class OfflineStorageContractTests
{
    protected abstract IOfflineStorage CreateStorage();

    [Fact]
    public async Task A_missing_key_reads_null()
        => Assert.Null(await CreateStorage().ReadAsync("app/none"));

    [Fact]
    public async Task A_value_is_written_replaced_and_deleted()
    {
        var storage = CreateStorage();

        await storage.WriteAsync("app/Person/row/1", [1, 2, 3]);
        await storage.WriteAsync("app/Person/row/1", [4, 5]);
        Assert.Equal(new byte[] { 4, 5 }, await storage.ReadAsync("app/Person/row/1"));

        await storage.DeleteAsync("app/Person/row/1");
        Assert.Null(await storage.ReadAsync("app/Person/row/1"));
        await storage.DeleteAsync("app/Person/row/1");      // deleting twice is fine
    }

    [Fact]
    public async Task Keys_are_exact()
    {
        var storage = CreateStorage();
        await storage.WriteAsync("app/Person/row/1", [1]);
        await storage.WriteAsync("app/Person/row/10", [10]);
        await storage.WriteAsync("app/person/row/1", [2]);       // case matters

        Assert.Equal(new byte[] { 1 }, await storage.ReadAsync("app/Person/row/1"));
        Assert.Equal(new byte[] { 10 }, await storage.ReadAsync("app/Person/row/10"));
        Assert.Equal(new byte[] { 2 }, await storage.ReadAsync("app/person/row/1"));
    }

    [Fact]
    public async Task An_empty_value_and_a_large_one_round_trip()
    {
        var storage = CreateStorage();
        var large = Enumerable.Range(0, 1_000_000).Select(i => (byte)i).ToArray();

        await storage.WriteAsync("app/empty", []);
        await storage.WriteAsync("app/large", large);

        Assert.Equal(Array.Empty<byte>(), await storage.ReadAsync("app/empty"));
        Assert.Equal(large, await storage.ReadAsync("app/large"));
    }
}

public sealed class MemoryOfflineStorageContractTests : OfflineStorageContractTests
{
    protected override IOfflineStorage CreateStorage() => new MemoryOfflineStorage();
}

public sealed class FileOfflineStorageContractTests : OfflineStorageContractTests, IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "queryeables-storage-" + Guid.NewGuid().ToString("N"));

    protected override IOfflineStorage CreateStorage() => new FileOfflineStorage(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }
}

public sealed class SqliteOfflineStorageContractTests : OfflineStorageContractTests, IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "queryeables-offline-" + Guid.NewGuid().ToString("N") + ".db");

    protected override IOfflineStorage CreateStorage() => new SqliteOfflineStorage($"Data Source={_file};Pooling=False");

    public void Dispose()
    {
        foreach (var file in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }
}

public sealed class EncryptedOfflineStorageContractTests : OfflineStorageContractTests
{
    protected override IOfflineStorage CreateStorage() => new EncryptedOfflineStorage(new MemoryOfflineStorage(), EncryptedOfflineStorage.NewKey());
}

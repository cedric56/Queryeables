using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Queryeables.Remote.Offline;

/// <summary>
/// Where the offline mode keeps its data between runs: the copy of the offline entities, the writes waiting for the
/// server, the conflicts and the current mode. A key-value store of byte arrays, so that it fits every platform: files on
/// desktop and mobile, the browser's storage with WebAssembly, memory in tests.
/// </summary>
public interface IOfflineStorage
{
    ValueTask<byte[]?> ReadAsync(string key, CancellationToken ct = default);

    ValueTask WriteAsync(string key, byte[] value, CancellationToken ct = default);

    ValueTask DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>In memory: lost when the application stops. For tests, or a session that does not need to survive a restart.</summary>
public sealed class MemoryOfflineStorage : IOfflineStorage
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public ValueTask<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        lock (_gate)
            return ValueTask.FromResult(_values.TryGetValue(key, out var value) ? value.ToArray() : null);
    }

    public ValueTask WriteAsync(string key, byte[] value, CancellationToken ct = default)
    {
        lock (_gate)
            _values[key] = value.ToArray();
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        lock (_gate)
            _values.Remove(key);
        return ValueTask.CompletedTask;
    }
}

/// <summary>One file per key in a folder: desktop and mobile applications. Each file is written whole, then renamed.</summary>
public sealed class FileOfflineStorage : IOfflineStorage
{
    private readonly string _folder;

    /// <param name="folder">Created when missing; for example under <c>Environment.SpecialFolder.LocalApplicationData</c>.</param>
    public FileOfflineStorage(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    public async ValueTask<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        var path = PathOf(key);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    public async ValueTask WriteAsync(string key, byte[] value, CancellationToken ct = default)
    {
        var path = PathOf(key);
        // A temporary name of its own (two writers of one key never share it), written through to the disk before it
        // replaces the file: never a half-written file, even after a power cut.
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await file.WriteAsync(value, ct);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            throw;
        }
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        File.Delete(PathOf(key));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A file name from the key, the same on every file system: lowercase letters, digits and <c>-</c> kept, anything else
    /// encoded, capitals included (Windows and macOS would not tell <c>Ana</c> from <c>ana</c>), and dots too (Windows drops
    /// a trailing one). A name too long for a file system becomes a hash of the key.
    /// </summary>
    internal static string FileNameOf(string key)
    {
        var name = new System.Text.StringBuilder(key.Length);
        foreach (var c in key)
            name.Append(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-' ? c : $"_{(int)c:x4}");
        return name.Length <= 200
            ? name.ToString()
            : "h_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
    }

    private string PathOf(string key) => Path.Combine(_folder, FileNameOf(key));
}

/// <summary>
/// The browser's <c>localStorage</c>, for applications compiled to WebAssembly (Blazor, Avalonia Browser, Uno): the data
/// survives a reload and works without network. Values are stored as Base64 text; browsers allow about 5 MB per site.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed partial class BrowserOfflineStorage : IOfflineStorage
{
    public ValueTask<byte[]?> ReadAsync(string key, CancellationToken ct = default)
        => ValueTask.FromResult(GetItem(key) is { } text ? Convert.FromBase64String(text) : null);

    public ValueTask WriteAsync(string key, byte[] value, CancellationToken ct = default)
    {
        SetItem(key, Convert.ToBase64String(value));
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        RemoveItem(key);
        return ValueTask.CompletedTask;
    }

    [JSImport("globalThis.localStorage.getItem")]
    private static partial string? GetItem(string key);

    [JSImport("globalThis.localStorage.setItem")]
    private static partial void SetItem(string key, string value);

    [JSImport("globalThis.localStorage.removeItem")]
    private static partial void RemoveItem(string key);
}

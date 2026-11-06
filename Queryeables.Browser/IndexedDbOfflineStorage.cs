using Queryeables.Remote.Offline;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Queryeables.Browser;

/// <summary>
/// The offline copy and its queue in IndexedDB, for Blazor WebAssembly: hundreds of megabytes where
/// <see cref="BrowserOfflineStorage"/> (localStorage) stops at about 5 MB, and writes that do not block the page.
/// Each write resolves once IndexedDB has committed it.
/// </summary>
/// <example>
/// <code>
/// builder.Services.UseQueryeables(q => q
///     .UseMagicOnion(url).AddGeneratedEntities()
///     .WithOfflineMode(o => o.UseStorage(new IndexedDbOfflineStorage()).Entity&lt;Person, long&gt;(p => p.Id)));
/// </code>
/// </example>
[SupportedOSPlatform("browser")]
public sealed partial class IndexedDbOfflineStorage : IOfflineStorage
{
    private const string Module = "queryeables-indexeddb";
    private static Task? _import;

    /// <param name="database">The IndexedDB database: one per application, or per user to keep them apart.</param>
    public IndexedDbOfflineStorage(string database = "queryeables-offline")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        Database = database;
    }

    public string Database { get; }

    public async ValueTask<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        await ImportAsync(ct);
        var text = await Get(Database, key).WaitAsync(ct);
        return text is null ? null : Convert.FromBase64String(text);
    }

    public async ValueTask WriteAsync(string key, byte[] value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        await ImportAsync(ct);
        await Set(Database, key, Convert.ToBase64String(value)).WaitAsync(ct);
    }

    public async ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        await ImportAsync(ct);
        await Remove(Database, key).WaitAsync(ct);
    }

    /// <summary>The module ships in this package (a static web asset), next to the application's own files.</summary>
    private static Task ImportAsync(CancellationToken ct)
        => (_import ??= JSHost.ImportAsync(Module, "../_content/Queryeables.Browser/queryeables-indexeddb.js", CancellationToken.None)).WaitAsync(ct);

    [JSImport("get", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string?> Get(string database, string key);

    [JSImport("set", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    private static partial Task Set(string database, string key, string value);

    [JSImport("remove", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    private static partial Task Remove(string database, string key);
}

using System.Security.Cryptography;
using System.Text;

namespace Queryeables.Remote.Offline;

/// <summary>
/// Encrypts the offline copy and the queue before they reach <paramref name="inner"/> (a file, SQLite...): AES-256-GCM per
/// value, and names hashed with HMAC-SHA256, so that neither the data nor its keys (type names, row keys) can be read
/// without the key, and a value moved to another name fails to decrypt instead of being read as another row.
/// </summary>
/// <example>
/// <code>
/// // One key per user, kept by the platform (Windows DPAPI, the macOS / iOS keychain, Android Keystore...).
/// var storage = new EncryptedOfflineStorage(new FileOfflineStorage(folder), keyOfThisUser);
/// var mode = new OfflineMode(transport, serializer, storage);
/// </code>
/// </example>
/// <remarks>
/// <list type="bullet">
/// <item>The key is the protection: keep it out of the storage and out of the code (a key store of the platform, or one
/// derived from the user's password with <see cref="Rfc2898DeriveBytes"/>). Losing it loses the copy, not the server's
/// data: the next copy is taken again.</item>
/// <item>A value that does not decrypt (another key, tampered with) throws <see cref="CryptographicException"/> instead of
/// being dropped: it may be an offline write not sent yet. With the right key it reads again; otherwise
/// <see cref="OfflineMode.DiscardPendingChangesAsync"/> starts over.</item>
/// <item>Not in the browser: Blazor WebAssembly has no AES-GCM (<see cref="AesGcm.IsSupported"/> is false). There, encrypt
/// with the Web Crypto API in your own <see cref="IOfflineStorage"/>, and remember that a script injected into the page
/// (XSS) reads what the page can read, encrypted or not.</item>
/// </list>
/// </remarks>
/// <param name="inner">Where the encrypted values go.</param>
/// <param name="key">32 random bytes (AES-256). The two keys used, for values and for names, are derived from it.</param>
[System.Runtime.Versioning.UnsupportedOSPlatform("browser")]
public sealed class EncryptedOfflineStorage(IOfflineStorage inner, byte[] key) : IOfflineStorage
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly IOfflineStorage _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly byte[] _valueKey = Derive(key, "queryeables-offline-values");
    private readonly byte[] _nameKey = Derive(key, "queryeables-offline-names");

    /// <summary>A new random key, to keep in the platform's key store.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    public async ValueTask<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var stored = await _inner.ReadAsync(Name(key), ct);
        return stored is null ? null : Decrypt(key, stored);
    }

    public ValueTask WriteAsync(string key, byte[] value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return _inner.WriteAsync(Name(key), Encrypt(key, value), ct);
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _inner.DeleteAsync(Name(key), ct);
    }

    private static byte[] Derive(byte[] key, string purpose)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
            throw new ArgumentException("The key must be 32 bytes (AES-256): EncryptedOfflineStorage.NewKey() makes one.", nameof(key));
        if (!AesGcm.IsSupported)
            throw new PlatformNotSupportedException(
                "AES-GCM is not available on this platform (Blazor WebAssembly): encrypt with the Web Crypto API in your own IOfflineStorage.");
        return HKDF.Expand(HashAlgorithmName.SHA256, key, 32, Encoding.UTF8.GetBytes(purpose));
    }

    /// <summary>The name under which a value is stored: the same for the same key, meaningless without it.</summary>
    private string Name(string key) => Convert.ToHexStringLower(HMACSHA256.HashData(_nameKey, Encoding.UTF8.GetBytes(key)));

    /// <summary>Version, nonce, tag, cipher text; the name is authenticated with the value.</summary>
    private byte[] Encrypt(string key, byte[] value)
    {
        var stored = new byte[1 + NonceSize + TagSize + value.Length];
        stored[0] = Version;
        var nonce = stored.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_valueKey, TagSize);
        aes.Encrypt(nonce, value, stored.AsSpan(1 + NonceSize + TagSize), stored.AsSpan(1 + NonceSize, TagSize), Encoding.UTF8.GetBytes(key));
        return stored;
    }

    private byte[] Decrypt(string key, byte[] stored)
    {
        if (stored.Length < 1 + NonceSize + TagSize || stored[0] != Version)
            throw Unreadable(key);
        var value = new byte[stored.Length - 1 - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(_valueKey, TagSize);
            aes.Decrypt(stored.AsSpan(1, NonceSize), stored.AsSpan(1 + NonceSize + TagSize), stored.AsSpan(1 + NonceSize, TagSize), value,
                Encoding.UTF8.GetBytes(key));
            return value;
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw Unreadable(key, ex);
        }
    }

    private static CryptographicException Unreadable(string key, Exception? inner = null)
        => new($"The offline value '{key}' cannot be decrypted with this key (another key, or altered).", inner);
}

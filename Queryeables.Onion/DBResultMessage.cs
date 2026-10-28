using MessagePack;
using Queryeables.Abstraction;

namespace Queryeables.Onion;

/// <summary>
/// Wire form of a server response for MagicOnion: serialized data on success, otherwise the kind and message
/// of the error. Converted to and from <see cref="DbResult"/>, which stays serializer-agnostic.
/// </summary>
[MessagePackObject]
public partial class DBResultMessage
{
    [Key(0)]
    public string? Exception { get; set; }

    [Key(1)]
    public bool Successful { get; set; }

    [Key(2)]
    public byte[]? Data { get; set; }

    [Key(3)]
    public EntityErrorCode ErrorCode { get; set; }

    /// <summary>Validation messages, one per broken rule.</summary>
    [Key(4)]
    public string[]? Errors { get; set; }

    public static DBResultMessage Success(byte[]? data = null) => new() { Successful = true, Data = data };

    public static DBResultMessage Failure(EntityErrorCode errorCode, string message, IReadOnlyList<string>? errors = null)
        => new() { Successful = false, ErrorCode = errorCode, Exception = message, Errors = errors?.ToArray() };

    /// <summary>Wire form of a <see cref="DbResult"/>, with the serialized data of a success.</summary>
    public static DBResultMessage From(DbResult result, byte[]? data = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Successful
            ? Success(data)
            : Failure(result.ErrorCode, result.Exception ?? string.Empty, result.Errors);
    }

    /// <summary>Converts a server-side exception into a response, with the right error kind.</summary>
    public static DBResultMessage FromException(Exception exception) => From(DbResult.FromException(exception));

    /// <summary>Runs a server operation and puts its result or its error into a response.</summary>
    public static async Task<DBResultMessage> RunAsync(Func<Task<byte[]?>> action)
    {
        try
        {
            return Success(await action());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FromException(ex);
        }
    }

    /// <summary>The response as a <see cref="DbResult"/> (the serialized data is not part of it).</summary>
    public DbResult ToDBResult()
        => Successful ? DbResult.Success() : DbResult.Failure(ErrorCode, Exception ?? "The remote call failed.", Errors);

    /// <summary>Client side: returns the data, or throws the server error as an <see cref="EntityStoreException"/>.</summary>
    public byte[]? GetDataOrThrow()
    {
        ToDBResult().ThrowIfFailed();
        return Data;
    }
}

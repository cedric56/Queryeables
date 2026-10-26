using MessagePack;
using Queryeables.Abstraction;

namespace Queryeables.Onion;

/// <summary>Server response: data on success, otherwise the kind and message of the error.</summary>
[MessagePackObject]
public partial class DBResult
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

    public static DBResult Success(byte[]? data = null) => new() { Successful = true, Data = data };

    public static DBResult Failure(EntityErrorCode errorCode, string message, IReadOnlyList<string>? errors = null)
        => new() { Successful = false, ErrorCode = errorCode, Exception = message, Errors = errors?.ToArray() };

    /// <summary>Converts a server-side exception into a response, with the right error kind.</summary>
    public static DBResult FromException(Exception exception) => exception switch
    {
        EntityStoreException store => Failure(store.ErrorCode, store.Message, store.Errors),
        ArgumentException or NotSupportedException or FormatException
            => Failure(EntityErrorCode.InvalidRequest, exception.Message),
        _ => Failure(EntityErrorCode.Unknown,
            exception.InnerException is null ? exception.Message : $"{exception.Message}\n{exception.InnerException.Message}")
    };

    /// <summary>Runs a server operation and puts its result or its error into a response.</summary>
    public static async Task<DBResult> RunAsync(Func<Task<byte[]?>> action)
    {
        try
        {
            return Success(await action());
        }
        catch (Exception ex)
        {
            return FromException(ex);
        }
    }

    /// <summary>Client side: returns the data, or throws the server error as an <see cref="EntityStoreException"/>.</summary>
    public byte[]? GetDataOrThrow()
    {
        if (Successful)
            return Data;

        var code = ErrorCode == EntityErrorCode.None ? EntityErrorCode.Unknown : ErrorCode;
        throw new EntityStoreException(code, Exception ?? "L'appel distant a échoué.", Errors);
    }
}

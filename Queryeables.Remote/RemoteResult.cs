using Queryeables.Abstraction;

namespace Queryeables.Remote;

/// <summary>
/// Server response, independent of the transport: serialized data on success, otherwise the kind and message of the error.
/// A transport carries it as is, or maps it to its own message type.
/// </summary>
public sealed class RemoteResult
{
    public bool Successful { get; init; }

    public byte[]? Data { get; init; }

    public EntityErrorCode ErrorCode { get; init; }

    /// <summary>Error message; null on success.</summary>
    public string? Exception { get; init; }

    /// <summary>Validation messages, one per broken rule.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    public static RemoteResult Success(byte[]? data = null) => new() { Successful = true, Data = data };

    public static RemoteResult Failure(EntityErrorCode errorCode, string message, IReadOnlyList<string>? errors = null)
        => new() { ErrorCode = errorCode, Exception = message, Errors = errors ?? [] };

    /// <summary>Wire form of a <see cref="DbResult"/>, with the serialized data of a success.</summary>
    public static RemoteResult From(DbResult result, byte[]? data = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Successful
            ? Success(data)
            : Failure(result.ErrorCode, result.Exception ?? string.Empty, result.Errors);
    }

    /// <summary>Converts a server-side exception into a response, with the right error kind.</summary>
    public static RemoteResult FromException(Exception exception) => From(DbResult.FromException(exception));

    /// <summary>Runs a server operation and puts its result or its error into a response.</summary>
    public static async Task<RemoteResult> RunAsync(Func<Task<byte[]?>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
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
    public DbResult ToDbResult()
        => Successful ? DbResult.Success() : DbResult.Failure(ErrorCode, Exception ?? "The remote call failed.", Errors);

    /// <summary>Client side: returns the data, or throws the server error as an <see cref="EntityStoreException"/>.</summary>
    public byte[]? GetDataOrThrow()
    {
        ToDbResult().ThrowIfFailed();
        return Data;
    }
}

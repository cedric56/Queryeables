using MessagePack;
using Queryeables.Abstraction;
using Queryeables.Remote;

namespace Queryeables.Onion;

/// <summary>MessagePack form of a <see cref="RemoteResult"/>, the server response.</summary>
[MessagePackObject]
public sealed class DBResultMessage
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

    public static DBResultMessage From(RemoteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new()
        {
            Successful = result.Successful,
            Data = result.Data,
            ErrorCode = result.ErrorCode,
            Exception = result.Exception,
            Errors = result.Errors.Count == 0 ? null : result.Errors.ToArray(),
        };
    }

    public RemoteResult ToRemoteResult()
        => Successful
            ? RemoteResult.Success(Data)
            : RemoteResult.Failure(ErrorCode, Exception ?? "The remote call failed.", Errors);
}

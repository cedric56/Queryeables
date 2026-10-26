namespace Queryeables.Abstraction;

/// <summary>Kind of failure, identical locally and remotely.</summary>
public enum EntityErrorCode
{
    None = 0,

    /// <summary>Unexpected error (database unavailable, bug...).</summary>
    Unknown = 1,

    /// <summary>The target row does not exist (in a batch, an update or delete with no effect).</summary>
    NotFound = 2,

    /// <summary>The user is not allowed to perform this operation.</summary>
    Forbidden = 3,

    /// <summary>The entity breaks its validation rules (see <see cref="EntityStoreException.Errors"/>).</summary>
    Validation = 4,

    /// <summary>The row was modified in the meantime by someone else (optimistic concurrency).</summary>
    Conflict = 5,

    /// <summary>The entity type is not exposed by the server.</summary>
    UnknownType = 6,

    /// <summary>Operation not supported by this store or this server.</summary>
    NotSupported = 7,

    /// <summary>Malformed request (invalid filter, page 0...).</summary>
    InvalidRequest = 8,
}

/// <summary>Failure of a store operation, with its kind so that the caller can react.</summary>
public class EntityStoreException : Exception
{
    public EntityStoreException(EntityErrorCode errorCode, string message,
        IReadOnlyList<string>? errors = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Errors = errors ?? [];
    }

    public EntityErrorCode ErrorCode { get; }

    /// <summary>Validation messages, one per broken rule.</summary>
    public IReadOnlyList<string> Errors { get; }
}

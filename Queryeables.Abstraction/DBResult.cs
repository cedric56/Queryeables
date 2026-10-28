namespace Queryeables.Abstraction;

/// <summary>
/// Outcome of a transaction (<see cref="IEntityTransaction"/>, <see cref="EntityStoreExtensions.RunInTransactionAsync(IEntityStore, Func{IEntityStore, Task}, CancellationToken)"/>):
/// success, or the kind and message of the error. Identical locally and remotely.
/// </summary>
/// <remarks>
/// Cancellation is not a failure: an <see cref="OperationCanceledException"/> is never turned into a
/// <see cref="DbResult"/> and propagates as usual.
/// </remarks>
/// <example>
/// <code>
/// var result = await store.RunInTransactionAsync(async s =>
/// {
///     await s.UpdateAsync(order);
///     await s.SyncChildrenAsync(previousLines, order.Lines, l => l.Id == 0, (a, b) => a.Id == b.Id);
/// });
/// if (!result.Successful)
///     ShowError(result.ErrorCode, result.Exception);
/// </code>
/// </example>
public class DbResult
{
    private static readonly DbResult s_success = new(true, EntityErrorCode.None, null, null);

    protected DbResult(bool successful, EntityErrorCode errorCode, string? exception, IReadOnlyList<string>? errors)
    {
        Successful = successful;
        ErrorCode = successful ? EntityErrorCode.None : errorCode == EntityErrorCode.None ? EntityErrorCode.Unknown : errorCode;
        Exception = exception;
        Errors = errors ?? [];
    }

    public bool Successful { get; }

    /// <summary>Kind of error; <see cref="EntityErrorCode.None"/> on success.</summary>
    public EntityErrorCode ErrorCode { get; }

    /// <summary>Error message; null on success.</summary>
    public string? Exception { get; }

    /// <summary>Validation messages, one per broken rule.</summary>
    public IReadOnlyList<string> Errors { get; }

    public static DbResult Success() => s_success;

    public static DbResult<T> Success<T>(T data) => new(data);

    public static DbResult Failure(EntityErrorCode errorCode, string message, IReadOnlyList<string>? errors = null)
        => new(false, errorCode, message, errors);

    public static DbResult<T> Failure<T>(EntityErrorCode errorCode, string message, IReadOnlyList<string>? errors = null)
        => new(errorCode, message, errors);

    /// <summary>Converts an exception into a failure, with the right error kind.</summary>
    public static DbResult FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            EntityStoreException store => Failure(store.ErrorCode, store.Message, store.Errors),
            ArgumentException or NotSupportedException or FormatException
                => Failure(EntityErrorCode.InvalidRequest, exception.Message),
            _ => Failure(EntityErrorCode.Unknown,
                exception.InnerException is null ? exception.Message : $"{exception.Message}\n{exception.InnerException.Message}")
        };
    }

    /// <summary>Runs <paramref name="action"/> and returns its outcome; exceptions become failures.</summary>
    public static async Task<DbResult> RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action();
            return Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FromException(ex);
        }
    }

    /// <summary>Runs <paramref name="action"/> and returns its value; exceptions become failures.</summary>
    public static async Task<DbResult<T>> RunAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            return Success(await action());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FromException(ex).As<T>();
        }
    }

    /// <summary>The same failure, typed. Only valid on a failure.</summary>
    public DbResult<T> As<T>()
    {
        if (Successful)
            throw new InvalidOperationException("Only a failure can be converted to another result type.");
        return this as DbResult<T> ?? new DbResult<T>(ErrorCode, Exception, Errors);
    }

    /// <summary>The error as an <see cref="EntityStoreException"/>, for code that prefers exceptions.</summary>
    public EntityStoreException ToException()
    {
        if (Successful)
            throw new InvalidOperationException("A success has no error.");
        return new EntityStoreException(ErrorCode, Exception ?? "The operation failed.", Errors);
    }

    /// <summary>Throws <see cref="EntityStoreException"/> on failure.</summary>
    public void ThrowIfFailed()
    {
        if (!Successful)
            throw ToException();
    }

    public override string ToString() => Successful ? "Success" : $"{ErrorCode}: {Exception}";
}

/// <summary><see cref="DbResult"/> that carries a value on success.</summary>
public sealed class DbResult<T> : DbResult
{
    internal DbResult(T data) : base(true, EntityErrorCode.None, null, null) => Data = data;

    internal DbResult(EntityErrorCode errorCode, string? exception, IReadOnlyList<string>? errors)
        : base(false, errorCode, exception, errors) { }

    /// <summary>Value on success; default on failure.</summary>
    public T? Data { get; }

    /// <summary>Returns the value, or throws <see cref="EntityStoreException"/> on failure.</summary>
    public T GetDataOrThrow()
    {
        ThrowIfFailed();
        return Data!;
    }
}

public static class DBResultTaskExtensions
{
    /// <summary>Awaits the result and throws <see cref="EntityStoreException"/> on failure.</summary>
    public static async Task OrThrow(this Task<DbResult> task)
        => (await task.ConfigureAwait(false)).ThrowIfFailed();

    /// <summary>Awaits the result and returns its value, or throws <see cref="EntityStoreException"/> on failure.</summary>
    public static async Task<T> OrThrow<T>(this Task<DbResult<T>> task)
        => (await task.ConfigureAwait(false)).GetDataOrThrow();
}

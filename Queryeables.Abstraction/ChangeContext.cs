namespace Queryeables.Abstraction;

/// <summary>
/// Client that initiated the current write (replaces the former DBContext.CurrentConnectionId).
/// The server opens it around each write; the change source reads it so that the client
/// can ignore its own changes.
/// </summary>
/// <example>
/// <code>
/// using (ChangeContext.Begin(connectionId))
///     await store.UpdateAsync(entity);
/// </code>
/// </example>
public static class ChangeContext
{
    private static readonly AsyncLocal<string?> _connectionId = new();

    /// <summary>Client that initiated the current write, or null.</summary>
    public static string? ConnectionId => _connectionId.Value;

    /// <summary>Sets the current client until Dispose, which restores the previous value.</summary>
    public static IDisposable Begin(string? connectionId)
    {
        var previous = _connectionId.Value;
        _connectionId.Value = connectionId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _connectionId.Value = previous;
        }
    }
}

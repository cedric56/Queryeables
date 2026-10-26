namespace Queryeables.Abstraction;

/// <summary>
/// Client à l'origine de l'écriture en cours (remplace l'ancien DBContext.CurrentConnectionId).
/// Le serveur l'ouvre autour de chaque écriture ; la source de changements le lit pour que le client
/// puisse ignorer ses propres modifications.
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

    /// <summary>Client à l'origine de l'écriture en cours, ou null.</summary>
    public static string? ConnectionId => _connectionId.Value;

    /// <summary>Définit le client courant jusqu'au Dispose, qui restaure la valeur précédente.</summary>
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

using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Abstraction;

/// <summary>What a graph is about to do to one of its children.</summary>
public enum GraphWrite
{
    Insert,
    Update,
    Delete,
}

/// <summary>
/// Checks each child an <see cref="EntityGraph{TParent, TKey}"/> writes, before it is written. A server sets one
/// (<see cref="GraphWriteGuard.Begin"/>) while it saves a graph sent by a remote client: the children then follow the
/// rules of their own type (access policy, row filter, <c>[ServerOwned]</c>, properties left out by the serializer), not
/// only those of their parent.
/// </summary>
public interface IGraphWriteGuard
{
    /// <summary>
    /// Throws <see cref="EntityStoreException"/> to refuse the write; may set the values the server decides on
    /// <paramref name="child"/>.
    /// </summary>
    /// <param name="stored">The stored row of an updated or deleted child.</param>
    Task CheckAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TChild>(IEntityStore store, GraphWrite write, TChild child,
        TChild? stored, CancellationToken ct) where TChild : class;
}

/// <summary>The <see cref="IGraphWriteGuard"/> of the current asynchronous flow.</summary>
public static class GraphWriteGuard
{
    private static readonly AsyncLocal<IGraphWriteGuard?> _current = new();

    /// <summary>The guard of the current flow, or null (a graph saved by the application's own code).</summary>
    public static IGraphWriteGuard? Current => _current.Value;

    /// <summary>Sets the guard until Dispose, which restores the previous one.</summary>
    public static IDisposable Begin(IGraphWriteGuard? guard)
    {
        var previous = _current.Value;
        _current.Value = guard;
        return new Scope(previous);
    }

    private sealed class Scope(IGraphWriteGuard? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _current.Value = previous;
        }
    }
}

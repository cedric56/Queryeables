using System.Data.Common;
using System.Runtime.CompilerServices;

namespace Queryeables.Dommel;

/// <summary>
/// Tells a connection factory that the writes of a connection are committed and visible to the other connections:
/// <see cref="DommelEntityStore"/> signals it after each write outside a transaction, and after each commit.
/// <para>
/// A change source that reports changes from inside the commit (the SQLite commit hook runs before the commit completes)
/// waits for this signal: a subscriber that reads the row again then sees its new state.
/// </para>
/// </summary>
public static class CommittedWrites
{
    private static readonly ConditionalWeakTable<DbConnection, Action> Handlers = new();

    /// <summary>Calls <paramref name="handler"/> each time the writes of <paramref name="connection"/> are committed.</summary>
    public static void OnCommitted(DbConnection connection, Action handler)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(handler);
        Handlers.AddOrUpdate(connection, handler);
    }

    /// <summary>The writes of <paramref name="connection"/> are committed.</summary>
    public static void Signal(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (Handlers.TryGetValue(connection, out var handler))
            handler();
    }
}

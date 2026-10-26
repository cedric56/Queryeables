using System.Collections.Concurrent;

namespace Queryeables.Abstraction;

public sealed class DbSession : IDbSession
{
    private readonly ConcurrentDictionary<Type, (string session, Func<Task> unsubscribe)> _sessions = new();

    public bool TryGetSession(Type type, out (string session, Func<Task> unsubscribe) session)
        => _sessions.TryGetValue(type, out session);

    public void SetSession(Type type, (string session, Func<Task> unsubscribe) session)
        => _sessions[type] = session;

    public bool RemoveSession(Type type)
        => _sessions.TryRemove(type, out _);
}

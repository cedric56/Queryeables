namespace Queryeables.Abstraction;

/// <summary>
/// Client side: subscription to change notifications (MagicOnion or SignalR) for each entity type.
/// The session id accompanies writes, so that the client ignores its own changes.
/// </summary>
public interface IDbSession
{
    bool TryGetSession(Type type, out (string session, Func<Task> unsubscribe) session);
    void SetSession(Type type, (string session, Func<Task> unsubscribe) session);
    bool RemoveSession(Type type);
}

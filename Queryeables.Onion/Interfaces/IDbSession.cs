namespace Queryeables.Onion;

/// <summary>
/// Côté client : abonnement au hub de changements pour chaque type d'entité.
/// L'identifiant de session accompagne les écritures, pour que le client ignore ses propres changements.
/// </summary>
public interface IDbSession
{
    bool TryGetSession(Type type, out (string session, Func<Task> unsubscribe) session);
    void SetSession(Type type, (string session, Func<Task> unsubscribe) session);
    bool RemoveSession(Type type);
}

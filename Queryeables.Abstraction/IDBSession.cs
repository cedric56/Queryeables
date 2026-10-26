using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Queryeables.Abstraction;

public interface IDBSession
{
    bool TryGetSession(Type type, out (string session, Func<Task> unsubscribe) session);
    void SetSession(Type type, (string session, Func<Task> unsubscribe) session);
    bool RemoveSession(Type type);
    void AddMonitor(IDbMonitor monitor);
    bool RemoveMonitor(IDbMonitor monitor);
    IReadOnlyList<IDbMonitor> GetMonitors();
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Queryeables.Abstraction;

public class DBSession : IDBSession
{
    private readonly ConcurrentDictionary<IDBMonitor, byte> _monitors = new();
    private readonly ConcurrentDictionary<Type, (string session, Func<Task> unsubscribe)> _sessions = new();

    public void AddMonitor(IDBMonitor monitor)
    {
        _monitors.TryAdd(monitor, 0);
    }

    public IReadOnlyList<IDBMonitor> GetMonitors()
        => _monitors.Keys.ToArray();

    public bool RemoveMonitor(IDBMonitor monitor)
        => _monitors.TryRemove(monitor, out _);

    public bool RemoveSession(Type type)
        => _sessions.TryRemove(type, out _);

    public void SetSession(Type type, (string session, Func<Task> unsubscribe) session)
        => _sessions[type] = session;

    public bool TryGetSession(Type type, out (string session, Func<Task> unsubscribe) session)
        => _sessions.TryGetValue(type, out session!);
}

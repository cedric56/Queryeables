using System.Threading;

namespace Queryeables.Abstraction;

public static class DBContext
{
    public static readonly AsyncLocal<string?> CurrentConnectionId = new();
}

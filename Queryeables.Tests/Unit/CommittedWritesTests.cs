using Microsoft.Data.Sqlite;
using Queryeables.Dommel;

namespace Queryeables.Tests.Unit;

/// <summary>The signal that tells the SQLite factory a commit has returned, so that it reports the changes then.</summary>
public class CommittedWritesTests
{
    [Fact]
    public void Only_the_connection_that_committed_is_signaled()
    {
        using var first = new SqliteConnection();
        using var second = new SqliteConnection();
        var signaled = new List<string>();
        CommittedWrites.OnCommitted(first, () => signaled.Add("first"));
        CommittedWrites.OnCommitted(second, () => signaled.Add("second"));

        CommittedWrites.Signal(first);
        CommittedWrites.Signal(first);

        Assert.Equal(new[] { "first", "first" }, signaled);
    }

    [Fact]
    public void A_connection_without_handler_is_ignored()
    {
        using var connection = new SqliteConnection();
        CommittedWrites.Signal(connection);   // no exception
    }
}

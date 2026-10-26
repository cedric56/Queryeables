using Avae.DAL;
using Dommel;
using System.Data;
using System.Data.Common;

namespace Example.Models;

public static class DbTransaction
{
    public static async Task<DBResult> RunAsync(
        DbProviderFactory factory,
        Func<IDbConnection, IDbTransaction, Task> work)
    {
        using var connection = factory.CreateConnection()!;
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            await work(connection, transaction);
            transaction.Commit();
            return new DBResult { Successful = true };
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            return new DBResult
            {
                Successful = false,
                Exception = string.Join("\n", ex.Message, ex.InnerException?.Message)
            };
        }
    }
}

public static class ChildSync
{
    public static void Sync<TChild>(
        IDbConnection connection, IDbTransaction transaction,
        IEnumerable<TChild> before, IEnumerable<TChild> current,
        Func<TChild, bool> isNew,
        Func<TChild, TChild, bool> matches)
        where TChild : class
    {
        var currentList = current as IReadOnlyCollection<TChild> ?? current.ToList();

        foreach (var child in currentList)
        {
            if (isNew(child))
                connection.Insert(child, transaction);
            else
                connection.Update(child, transaction);
        }

        foreach (var old in before)
            if (!currentList.Any(c => matches(c, old)))
                connection.Delete(old, transaction);
    }
}



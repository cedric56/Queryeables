using Dommel;
using MessagePack;
using Queryeables.Abstraction;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables.Onion;

public abstract class EntityHandler
{
    public abstract Type Type { get; }
    public abstract Type Enumerable { get; }

    public abstract Task<object> GetAllAsync(CancellationToken cancellationToken = default);
    public abstract Task<object?> GetAsync(long id, CancellationToken cancellationToken = default);
    public abstract Task<object> FindByAnyAsync(Dictionary<string, object> filters, CancellationToken cancellationToken = default);
    public abstract Task<object> WhereAsync(Dictionary<string, object> filters, CancellationToken cancellationToken = default);

    public abstract Task<DBResult> SaveAsync(byte[] bytes, CancellationToken cancellationToken = default);

    public abstract Task<DBResult> RemoveAsync(byte[] bytes, CancellationToken cancellationToken = default);
}

public class EntityHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
    DbProviderFactory factory,
    IDbTransaction<T>? dbTransaction = null) : EntityHandler where T : class, new()
{
    public override Type Type => typeof(T);

    public override Type Enumerable => typeof(IEnumerable<T>);

    public override async Task<object> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = factory.CreateConnection()!;
        await connection.OpenAsync(cancellationToken);
        return await connection.GetAllAsync<T>(cancellationToken: cancellationToken);
    }
    public override async Task<object?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        using var connection = factory.CreateConnection()!;
        await connection.OpenAsync(cancellationToken);
        return await connection.GetAsync<T>(id, cancellationToken: cancellationToken);
    }

    private static Expression<Func<T, bool>> BuildPredicate(Dictionary<string, object> filters, bool any)
    {
        var param = Expression.Parameter(typeof(T), "e");
        Expression? body = null;

        foreach (var (key, value) in filters)
        {
            var property = typeof(T).GetProperty(key)
                ?? throw new ArgumentException($"Unknown property '{key}' on {typeof(T).Name}.");

            var equal = Expression.Equal(
                Expression.Property(param, property),
                Expression.Constant(value, property.PropertyType));

            body = body is null ? equal : any ? Expression.OrElse(body, equal) : Expression.AndAlso(body, equal);
        }

        body ??= Expression.Constant(true); // filtres vides -> tout retourner, même comportement que Create<T> avant
        return Expression.Lambda<Func<T, bool>>(body, param);
    }

    public override async Task<object> FindByAnyAsync(Dictionary<string, object> filters, CancellationToken cancellationToken = default)
    {
        var predicate = BuildPredicate(filters, any: true);
        using var connection = factory.CreateConnection()!;
        await connection.OpenAsync(cancellationToken);
        return await connection.SelectAsync(predicate, transaction: null, cancellationToken: cancellationToken);
    }

    public override async Task<object> WhereAsync(Dictionary<string, object> filters, CancellationToken cancellationToken = default)
    {
        var predicate = BuildPredicate(filters, any: false);
        using var connection = factory.CreateConnection()!;
        await connection.OpenAsync(cancellationToken);
        return await connection.SelectAsync(predicate, transaction: null, cancellationToken: cancellationToken);
    }

    public override Task<DBResult> SaveAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (dbTransaction == null)
            return Task.FromResult(new DBResult()
            {
                Successful = false
            });

        var entity = MessagePackSerializer.Deserialize<T>(bytes, cancellationToken: cancellationToken);
        return dbTransaction.SaveAsync(entity, cancellationToken);
    }

    public override Task<DBResult> RemoveAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (dbTransaction == null)
            return Task.FromResult(new DBResult()
            {
                Successful = false
            });
        var entity = MessagePackSerializer.Deserialize<T>(bytes, cancellationToken: cancellationToken);
        return dbTransaction.RemoveAsync(entity, cancellationToken);
    }
}

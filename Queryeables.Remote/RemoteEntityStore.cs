using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Remote;

/// <summary>
/// The store of a remote client: it reads what the server publishes, for the caches of the client (its replicas). Every
/// row the user may read (<see cref="GetAllAsync{TEntity}"/>), the rows changed since a date, and a row by its key (what a
/// cache reads again when a change is notified). The server applies its access policy and row filter to each.
/// </summary>
/// <remarks>
/// <para>
/// <b>No query and no write.</b> A client queries its replica in memory (<c>IEntityCache</c>), and writes by calling a
/// service of the application, which uses its own stores on the server. Any other read, and every write, throws
/// <see cref="EntityStoreException"/> with <see cref="EntityErrorCode.NotSupported"/>. The package
/// <c>Queryeables.Offline</c> adds the generic writes and filters (and the offline mode), on both sides.
/// </para>
/// <para>
/// <b>Errors:</b> reads throw <see cref="EntityStoreException"/>, with the kind of error (permissions, limits...).
/// </para>
/// </remarks>
/// <param name="remote">The transport.</param>
/// <param name="serializer">The entity serializer, the same as the server's.</param>
public sealed class RemoteEntityStore(IEntityTransport remote, IEntitySerializer serializer) : IEntityStore
{
    private readonly IEntityTransport _remote = remote ?? throw new ArgumentNullException(nameof(remote));
    private readonly IEntitySerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

    /// <summary>A factory of remote stores (<see cref="IEntityStoreFactory.IsRemote"/> is true).</summary>
    public static IEntityStoreFactory Factory(IEntityTransport remote, IEntitySerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(serializer);
        return new EntityStoreFactory(() => new RemoteEntityStore(remote, serializer), isRemote: true);
    }

    // ---------- What the server publishes ----------

    public async Task<IReadOnlyList<TEntity>> GetAllAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(CancellationToken ct = default)
        where TEntity : class
        => DeserializeMany<TEntity>(await CallAsync("get_all", Name<TEntity>(), () => _remote.GetAllAsync(Name<TEntity>(), ct)));

    /// <summary>Rows modified since <paramref name="sinceUtc"/>, soft-deleted ones included.</summary>
    public async Task<IReadOnlyList<TEntity>> SelectChangesAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(DateTime sinceUtc,
        CancellationToken ct = default)
        where TEntity : class
        => DeserializeMany<TEntity>(
            await CallAsync("get_changes", Name<TEntity>(), () => _remote.GetChangesAsync(Name<TEntity>(), DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc), ct)));

    /// <summary>The server's changes, then <paramref name="predicate"/> in memory: a predicate does not travel.</summary>
    public async Task<IReadOnlyList<TEntity>> SelectChangesAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(DateTime sinceUtc,
        Expression<Func<TEntity, bool>> predicate, int maxRows, CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var match = predicate.Compile(preferInterpretation: true);
        return [.. (await SelectChangesAsync<TEntity>(sinceUtc, ct)).Where(match).Take(maxRows)];
    }

    /// <summary>The rows of one key (<c>e =&gt; e.Id == id</c>, every column of a composite key); any other predicate is refused.</summary>
    public async Task<IReadOnlyList<TEntity>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        var conditions = KeyConditions(predicate);
        return DeserializeMany<TEntity>(await CallAsync("select", Name<TEntity>(), () => _remote.SelectAsync(Name<TEntity>(), conditions, false, ct)));
    }

    /// <inheritdoc cref="SelectAsync{TEntity}(Expression{Func{TEntity, bool}}, CancellationToken)"/>
    public async Task<TEntity?> FirstOrDefaultAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => (await SelectAsync(predicate, ct)).FirstOrDefault();

    /// <summary>One page of the rows of one key: see <see cref="SelectAsync{TEntity}(Expression{Func{TEntity, bool}}, CancellationToken)"/>.</summary>
    public async Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize, CancellationToken ct = default) where TEntity : class
    {
        Paging.Validate(pageNumber, pageSize);
        return [.. (await SelectAsync(predicate, ct)).Skip((pageNumber - 1) * pageSize).Take(pageSize)];
    }

    // ---------- Refused ----------

    public Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
        => throw NoWrites();

    public Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
        => throw NoWrites();

    public Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
        => throw NoWrites();

    public Task<bool> PatchAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity original, TEntity modified,
        CancellationToken ct = default) where TEntity : class
        => throw NoWrites();

    public Task<int> UpdateWhereAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(Expression<Func<TEntity, bool>> predicate,
        EntityUpdate<TEntity> update, CancellationToken ct = default) where TEntity : class
        => throw NoWrites();

    public Task<int> DeleteWhereAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(Expression<Func<TEntity, bool>> predicate,
        CancellationToken ct = default) where TEntity : class
        => throw NoWrites();

    public Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default) => throw NoWrites();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
    }

    private static EntityStoreException NoWrites()
        => new(EntityErrorCode.NotSupported,
            "A remote client does not write through its store: call a service of the application, or enable the writes of " +
            "Queryeables.Offline (UseRemoteWrites on the client, AllowWrites on the server).");

    /// <summary><c>e.Key == value</c> (and <c>&amp;&amp;</c> for a composite key): the conditions that read one row.</summary>
    private static FilterCondition[] KeyConditions<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var keys = SyncMetadata.Keys(typeof(TEntity));
        var conditions = new List<FilterCondition>();
        if (keys.Count == 0 || !Collect(predicate.Body, predicate.Parameters[0], conditions)
            || conditions.Count != keys.Count
            || !keys.All(k => conditions.Count(c => c.Property == k.Name) == 1))
            throw new EntityStoreException(EntityErrorCode.NotSupported,
                $"A remote client reads {typeof(TEntity).Name} by key only (e => e.Id == id): query its cache (IEntityCache) in memory, " +
                "or call a service of the application.");
        return [.. conditions];
    }

    private static bool Collect(Expression body, ParameterExpression parameter, List<FilterCondition> conditions)
    {
        switch (body)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso } and:
                return Collect(and.Left, parameter, conditions) && Collect(and.Right, parameter, conditions);
            case BinaryExpression { NodeType: ExpressionType.Equal } equal:
                if (Member(equal.Left, parameter) is { } left && Value(equal.Right, parameter, out var right))
                    conditions.Add(new FilterCondition(left.Name, FilterOperator.Equal, right));
                else if (Member(equal.Right, parameter) is { } member && Value(equal.Left, parameter, out var value))
                    conditions.Add(new FilterCondition(member.Name, FilterOperator.Equal, value));
                else
                    return false;
                return true;
            default:
                return false;
        }
    }

    private static PropertyInfo? Member(Expression expression, ParameterExpression parameter)
        => Unwrap(expression) is MemberExpression { Member: PropertyInfo property } member && member.Expression == parameter ? property : null;

    /// <summary>A value that does not depend on the row: a constant, or a captured variable.</summary>
    private static bool Value(Expression expression, ParameterExpression parameter, out object? value)
    {
        value = null;
        return !Uses(expression, parameter) && CapturedValues.TryEvaluate(Unwrap(expression), out value);
    }

    private static bool Uses(Expression expression, ParameterExpression parameter)
    {
        var finder = new ParameterFinder(parameter);
        finder.Visit(expression);
        return finder.Found;
    }

    private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found |= node == parameter;
            return node;
        }
    }

    private static Expression Unwrap(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert ? Unwrap(convert.Operand) : expression;

    private static async Task<byte[]?> CallAsync(string operation, string type, Func<Task<TransportResult>> call)
        => (await RemoteDiagnostics.ClientAsync(operation, type, call)).GetDataOrThrow();

    /// <summary>Name under which the server registers the type (see <see cref="EntityName"/>).</summary>
    private static string Name<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>() => EntityName.Of<TEntity>();

    private IReadOnlyList<TEntity> DeserializeMany<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(byte[]? data)
        => data is { Length: > 0 } ? _serializer.Deserialize<TEntity[]>(data) ?? [] : [];
}

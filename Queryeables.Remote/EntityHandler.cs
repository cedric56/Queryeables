using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables.Remote;

/// <summary>
/// Handles, server side, the requests for one entity type through <see cref="IEntityStore"/>. Errors are returned in the
/// <see cref="TransportResult"/>, with their kind. Entities are read and written with the <see cref="IEntitySerializer"/> of
/// the server, given by <see cref="EntityRequestDispatcher"/>.
/// </summary>
/// <remarks>
/// <see cref="EntityHandler{T}"/> serves the replicas of the clients: every row the user may read, the changes since a date,
/// rows by key, and nothing else.
/// </remarks>
public abstract class EntityHandler
{
    /// <summary>The default limit of a replica: most rows a read returns.</summary>
    public const int DefaultMaxReadSize = 10_000;

    public abstract Type Type { get; }

    /// <summary>
    /// Why <paramref name="serializer"/> cannot carry this type safely (a property marked to be left out by an attribute the
    /// serializer does not honor: its value would reach every client), or null.
    /// </summary>
    public virtual string? CheckSerializer(IEntitySerializer serializer) => null;

    /// <summary>The properties hidden by an attribute <paramref name="serializer"/> ignores, as a message; null when none.</summary>
    public static string? CheckHidden<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(IEntitySerializer serializer)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(serializer);
        var leaking = RemoteMembers<TEntity>.MarkedHidden.Where(p => !serializer.Omits(p)).Select(p => p.Name).ToList();
        return leaking.Count == 0
            ? null
            : $"{typeof(TEntity).Name}.{string.Join(", ", leaking)} is marked to be left out of serialization by an attribute that "
              + $"{serializer.GetType().Name} does not honor: every client would read it. Use the attribute of this serializer "
              + "([IgnoreMember] for MessagePack, [JsonIgnore] for System.Text.Json).";
    }

    /// <summary>Every row the user may read.</summary>
    public abstract Task<TransportResult> GetAllAsync(IEntitySerializer serializer, EntityRequestContext context, CancellationToken ct = default);

    /// <summary>The rows of one key, among those the user may read.</summary>
    public abstract Task<TransportResult> SelectAsync(IEntitySerializer serializer, FilterCondition[] conditions,
        EntityRequestContext context, CancellationToken ct = default);

    /// <summary>The rows changed since <paramref name="sinceUtc"/>, among those the user may read.</summary>
    public abstract Task<TransportResult> GetChangesAsync(IEntitySerializer serializer, DateTime sinceUtc, EntityRequestContext context,
        CancellationToken ct = default);
}

/// <summary>The rows of its type a relation filter of another type may read, for a user (see <see cref="HandlerRelationGuard"/>).</summary>
public interface IRelationRestrictor<T> where T : class
{
    Task<Expression<Func<T, bool>>> RestrictRelationAsync(Expression<Func<T, bool>> predicate, EntityRequestContext context, CancellationToken ct);
}

/// <summary>
/// Serves the replica of <typeparamref name="T"/> to the clients: every row the user may read (at most
/// <c>maxReadSize</c>), the rows changed since a date, and rows by key (what a cache reads again on a notification).
/// Any other condition is refused (<see cref="EntityErrorCode.InvalidRequest"/>).
/// </summary>
/// <example>
/// <code>
/// registry.Register(new EntityHandler&lt;Customer&gt;(stores, EntityAccessPolicies.Authenticated,
///     RowLevelSecurity.For&lt;Customer, long&gt;(c =&gt; c.Id, user =&gt; c =&gt; c.OwnerId == user.Id())));
/// </code>
/// </example>
/// <param name="storeFactory">Creates a store per call; the handler disposes it.</param>
/// <param name="policy">Who may read the type. <see cref="EntityAccessPolicies.AllowAll"/> to allow everyone.</param>
/// <param name="rows">The rows each user may read (see <see cref="RowLevelSecurity{T}"/>); every row when null.</param>
/// <param name="maxReadSize">
/// Most rows a replica may hold. Beyond it, the read fails with <see cref="EntityErrorCode.InvalidRequest"/> instead of
/// sending the table: such a type is read through a service of the application.
/// </param>
public sealed class EntityHandler<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(
    Func<IEntityStore> storeFactory,
    IEntityAccessPolicy policy,
    RowLevelSecurity<T>? rows = null,
    int maxReadSize = EntityHandler.DefaultMaxReadSize) : EntityHandler, IRelationRestrictor<T> where T : class
{
    /// <inheritdoc cref="EntityHandler{T}"/>
    public EntityHandler(IEntityStoreFactory stores, IEntityAccessPolicy policy, RowLevelSecurity<T>? rows = null,
        int maxReadSize = DefaultMaxReadSize)
        : this((stores ?? throw new ArgumentNullException(nameof(stores))).Create, policy, rows, maxReadSize)
    {
    }

    private readonly Func<IEntityStore> _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
    private readonly IEntityAccessPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly int _maxReadSize = maxReadSize > 0 ? maxReadSize : throw new ArgumentOutOfRangeException(nameof(maxReadSize));
    private static readonly Expression<Func<T, bool>> All = _ => true;

    public override Type Type => typeof(T);

    public override string? CheckSerializer(IEntitySerializer serializer) => CheckHidden<T>(serializer);

    public override Task<TransportResult> GetAllAsync(IEntitySerializer serializer, EntityRequestContext context, CancellationToken ct = default)
        => ReadAsync(serializer, All, context, ct);

    /// <summary>Rows by key only (<c>Id == 7</c>, every column of a composite key): what a cache reads again.</summary>
    public override Task<TransportResult> SelectAsync(IEntitySerializer serializer, FilterCondition[] conditions,
        EntityRequestContext context, CancellationToken ct = default)
        => (conditions?.Length ?? 0) == 0
            ? GetAllAsync(serializer, context, ct)
            : TransportResult.RunAsync(async () =>
            {
                var predicate = KeyPredicate(conditions!);
                return await ReadCoreAsync(serializer, predicate, context, ct);
            });

    public override Task<TransportResult> GetChangesAsync(IEntitySerializer serializer, DateTime sinceUtc, EntityRequestContext context,
        CancellationToken ct = default)
        => TransportResult.RunAsync(async () =>
        {
            using var relations = RelationReadGuard.Begin(new HandlerRelationGuard(context));
            await AuthorizeAsync(context, ct);
            await using var store = _storeFactory();

            // The user's rows only, and one more than allowed at most, both in the query.
            var changes = await store.SelectChangesAsync(sinceUtc, Restrict(All, context), _maxReadSize + 1, ct);
            return changes.Count > _maxReadSize ? throw TooMany() : serializer.Serialize(changes.ToArray());
        });

    /// <summary>The related rows a filter of another type reads: those of this type the user may read.</summary>
    public async Task<Expression<Func<T, bool>>> RestrictRelationAsync(Expression<Func<T, bool>> predicate, EntityRequestContext context,
        CancellationToken ct)
    {
        await AuthorizeAsync(context, ct);
        return Restrict(predicate, context);
    }

    private Task<TransportResult> ReadAsync(IEntitySerializer serializer, Expression<Func<T, bool>> predicate, EntityRequestContext context,
        CancellationToken ct)
        => TransportResult.RunAsync(() => ReadCoreAsync(serializer, predicate, context, ct));

    private async Task<byte[]?> ReadCoreAsync(IEntitySerializer serializer, Expression<Func<T, bool>> predicate, EntityRequestContext context,
        CancellationToken ct)
    {
        using var relations = RelationReadGuard.Begin(new HandlerRelationGuard(context));
        await AuthorizeAsync(context, ct);
        await using var store = _storeFactory();
        var found = await store.SelectPagedAsync(Restrict(predicate, context), 1, _maxReadSize + 1, ct);
        return found.Count > _maxReadSize ? throw TooMany() : serializer.Serialize(found.ToArray());
    }

    /// <summary><c>key == value</c> for every column of the key, and nothing else.</summary>
    private static Expression<Func<T, bool>> KeyPredicate(FilterCondition[] conditions)
    {
        var keys = SyncMetadata.Keys(typeof(T));
        if (keys.Count == 0 || conditions.Length != keys.Count
            || conditions.Any(c => c.Operator != FilterOperator.Equal)
            || conditions.Select(c => c.Property).Distinct(StringComparer.Ordinal).Count() != keys.Count)
            throw KeysOnly();

        var parameter = Expression.Parameter(typeof(T), "e");
        Expression? body = null;
        foreach (var condition in conditions)
        {
            var key = keys.FirstOrDefault(k => k.Name == condition.Property) ?? throw KeysOnly();
            var value = FilterValues.ConvertTo(condition.Value, key.PropertyType, key.Name);
            var equal = Expression.Equal(Expression.Property(parameter, key), Expression.Constant(value, key.PropertyType));
            body = body is null ? equal : Expression.AndAlso(body, equal);
        }
        return Expression.Lambda<Func<T, bool>>(body!, parameter);
    }

    private static ArgumentException KeysOnly()
        => new($"A client reads {typeof(T).Name} by key only (every column of its key, compared with =): it queries its replica in memory.");

    private Expression<Func<T, bool>> Restrict(Expression<Func<T, bool>> predicate, EntityRequestContext context)
        => rows is null ? predicate : rows.Restrict(predicate, context.User);

    private async Task AuthorizeAsync(EntityRequestContext context, CancellationToken ct)
    {
        if (!await _policy.IsAllowedAsync(new EntityAccessRequest(typeof(T), context.User), ct))
            throw new EntityStoreException(EntityErrorCode.Forbidden, $"Access denied: {typeof(T).Name}.");
    }

    private EntityStoreException TooMany()
        => new(EntityErrorCode.InvalidRequest,
            $"More than {_maxReadSize} {typeof(T).Name} rows: too many for a replica. Read them through a service of the application.");
}

/// <summary>
/// The rows a relation filter reads, on a server: the related type must be exposed and readable by the user, and only its rows
/// the user may read count. A filter cannot reveal through a relation what the user could not read directly.
/// </summary>
public sealed class HandlerRelationGuard(EntityRequestContext context) : IRelationReadGuard
{
    public async Task<Expression<Func<TChild, bool>>> RestrictAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TChild>(
        Expression<Func<TChild, bool>> predicate, CancellationToken ct) where TChild : class
        => context.Handlers is { } handlers && handlers.TryGet(EntityName.Of<TChild>(), out var handler) && handler is IRelationRestrictor<TChild> typed
            ? await typed.RestrictRelationAsync(predicate, context, ct).ConfigureAwait(false)
            : throw new EntityStoreException(EntityErrorCode.Forbidden, $"{typeof(TChild).Name} is not exposed: a filter cannot go through it.");
}

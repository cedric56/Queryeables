using Queryeables.Abstraction;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables.Remote;

/// <summary>
/// Handles, server side, the requests for one entity type through <see cref="IEntityStore"/>: it therefore works with
/// Dommel as well as EF. Each call goes through the access policy, then validation for writes.
/// Errors are returned in the <see cref="TransportResult"/>, with their kind. Entities are read and written with the
/// <see cref="IEntitySerializer"/> of the server, given by <see cref="EntityRequestDispatcher"/>.
/// </summary>
public abstract class EntityHandler
{
    /// <summary>The default limit of a read that is not paged.</summary>
    public const int DefaultMaxReadSize = 10_000;

    public abstract Type Type { get; }

    /// <summary>
    /// Why <paramref name="serializer"/> cannot carry this type safely (a property marked to be left out by an attribute the
    /// serializer does not honor: its value would reach every client), or null.
    /// </summary>
    public virtual string? CheckSerializer(IEntitySerializer serializer) => null;

    public abstract Task<TransportResult> GetAllAsync(IEntitySerializer serializer, EntityRequestContext context, CancellationToken ct = default);

    public abstract Task<TransportResult> SelectAsync(IEntitySerializer serializer, FilterCondition[] conditions, bool any,
        EntityRequestContext context, CancellationToken ct = default);

    public abstract Task<TransportResult> SelectPagedAsync(IEntitySerializer serializer, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        EntityRequestContext context, CancellationToken ct = default);

    /// <summary>A page in the given order. The properties of the sort are checked against the entity type.</summary>
    public virtual Task<TransportResult> SelectPagedAsync(IEntitySerializer serializer, FilterCondition[] conditions, bool any, int pageNumber,
        int pageSize, SortKey[] sort, EntityRequestContext context, CancellationToken ct = default)
        => sort is not { Length: > 0 }
            ? SelectPagedAsync(serializer, conditions, any, pageNumber, pageSize, context, ct)
            : Task.FromResult(TransportResult.Failure(EntityErrorCode.NotSupported, $"{Type.Name} does not support sorted pages."));

    public abstract Task<TransportResult> GetChangesAsync(IEntitySerializer serializer, DateTime sinceUtc, EntityRequestContext context, CancellationToken ct = default);

    /// <summary>Standalone write, in its own store.</summary>
    public abstract Task<TransportResult> WriteAsync(IEntitySerializer serializer, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, CancellationToken ct = default);

    /// <summary>
    /// Write in a given store, for example during the transaction of a batch.
    /// Throws <see cref="EntityStoreException"/> on failure, which rolls back the batch.
    /// </summary>
    /// <param name="requireEffect">When true, an update or delete with no effect is an error (NotFound).</param>
    public abstract Task<byte[]> WriteAsync(IEntitySerializer serializer, IEntityStore store, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, bool requireEffect, CancellationToken ct = default);

    /// <summary>Counts, aggregates, projections, patches, writes of many rows, graph saves (<see cref="RemoteRequestKind"/>).</summary>
    public virtual Task<TransportResult> ExecuteAsync(IEntitySerializer serializer, RemoteRequest request, EntityRequestContext context,
        CancellationToken ct = default)
        => Task.FromResult(TransportResult.Failure(EntityErrorCode.NotSupported, $"{Type.Name} does not support this request."));

    /// <summary>The properties of this type a filter may name, and its navigations toward other exposed types.</summary>
    internal abstract IFilterScope FilterScope(IEntityHandlerRegistry? registry);

    /// <summary>True when <paramref name="type"/> is a collection of this type (<c>List&lt;T&gt;</c>, <c>IEnumerable&lt;T&gt;</c>...).</summary>
    internal abstract bool IsCollectionOf(Type type);
}

/// <example>
/// <code>
/// registry.Register(new EntityHandler&lt;Customer&gt;(stores, EntityAccessPolicies.Authenticated));
/// </code>
/// </example>
/// <param name="storeFactory">Creates a store per call; the handler disposes it.</param>
/// <param name="policy">Access policy, mandatory. <see cref="EntityAccessPolicies.AllowAll"/> to allow everything.</param>
/// <param name="validate">Validates entities (DataAnnotations) before insert and update. True by default.</param>
/// <param name="maxPageSize">
/// Maximum page size: a larger page is reduced to this size (idea taken from Datasync, see CREDITS.md).
/// </param>
/// <param name="rows">The rows each user may reach (see <see cref="RowLevelSecurity{T}"/>); every row when null.</param>
/// <param name="maxReadSize">
/// Most rows a read that is not paged may return (every row, a filter, the changes since a date). Beyond it, the read
/// fails with <see cref="EntityErrorCode.InvalidRequest"/> instead of sending the table: the client pages, or narrows its
/// filter.
/// </param>
/// <param name="aggregate">
/// When provided, standalone writes go through this service instead of the store: insert and update call
/// <see cref="IAggregateRepository{TEntity}.SaveAsync"/>, delete calls <see cref="IAggregateRepository{TEntity}.RemoveAsync"/>.
/// Use it to save an entity together with its children (a person and its contacts) in one transaction.
/// Access policy and validation still apply. In a batch, a graph repository (<see cref="IGraphRepository{TEntity}"/>) runs
/// its graph in the batch's transaction; any other service opens its own transaction, so its type cannot be part of a batch.
/// </param>
public sealed class EntityHandler<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(
    Func<IEntityStore> storeFactory,
    IEntityAccessPolicy policy,
    bool validate = true,
    int maxPageSize = 1000,
    IAggregateRepository<T>? aggregate = null,
    RowLevelSecurity<T>? rows = null,
    int maxReadSize = EntityHandler.DefaultMaxReadSize) : EntityHandler where T : class
{
    /// <inheritdoc cref="EntityHandler{T}"/>
    public EntityHandler(IEntityStoreFactory stores, IEntityAccessPolicy policy, bool validate = true, int maxPageSize = 1000,
        IAggregateRepository<T>? aggregate = null, RowLevelSecurity<T>? rows = null, int maxReadSize = DefaultMaxReadSize)
        : this((stores ?? throw new ArgumentNullException(nameof(stores))).Create, policy, validate, maxPageSize, aggregate, rows,
            maxReadSize)
    {
    }

    private readonly Func<IEntityStore> _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
    private readonly IEntityAccessPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly int _maxPageSize = maxPageSize > 0 ? maxPageSize : throw new ArgumentOutOfRangeException(nameof(maxPageSize));
    private readonly int _maxReadSize = maxReadSize > 0 ? maxReadSize : throw new ArgumentOutOfRangeException(nameof(maxReadSize));
    private static readonly Expression<Func<T, bool>> All = _ => true;

    public override Type Type => typeof(T);

    public override string? CheckSerializer(IEntitySerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        var leaking = RemoteMembers<T>.MarkedHidden.Where(p => !serializer.Omits(p)).Select(p => p.Name).ToList();
        return leaking.Count == 0
            ? null
            : $"{typeof(T).Name}.{string.Join(", ", leaking)} is marked to be left out of serialization by an attribute that "
              + $"{serializer.GetType().Name} does not honor: every client would read it. Use the attribute of this serializer "
              + "([IgnoreMember] for MessagePack, [JsonIgnore] for System.Text.Json).";
    }

    // The properties the serializer leaves out, per serializer: a client never sends them, an update keeps their stored value.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo[]> Omitted = new();

    private static System.Reflection.PropertyInfo[] OmittedBy(IEntitySerializer serializer)
        => Omitted.GetOrAdd(serializer.GetType(), _ => [.. RemoteMembers<T>.MarkedHidden.Where(serializer.Omits)]);

    // ---------- Read ----------

    public override Task<TransportResult> GetAllAsync(IEntitySerializer serializer, EntityRequestContext context, CancellationToken ct = default)
        => TransportResult.RunAsync(async () =>
        {
            using var relations = RelationReadGuard.Begin(new HandlerRelationGuard(context));
            await AuthorizeAsync(EntityOperation.Read, null, context, ct);
            await using var store = _storeFactory();
            return SerializeMany(serializer, await ReadBoundedAsync(store, Restrict(All, context), ct));
        });

    public override Task<TransportResult> SelectAsync(IEntitySerializer serializer, FilterCondition[] conditions, bool any,
        EntityRequestContext context, CancellationToken ct = default)
        => (conditions?.Length ?? 0) == 0
            ? GetAllAsync(serializer, context, ct)
            : TransportResult.RunAsync(async () =>
            {
                using var relations = RelationReadGuard.Begin(new HandlerRelationGuard(context));
                var predicate = Predicate(conditions!, any, context);
                await AuthorizeAsync(EntityOperation.Read, null, context, ct);
                await using var store = _storeFactory();
                return SerializeMany(serializer, await ReadBoundedAsync(store, Restrict(predicate, context), ct));
            });

    public override Task<TransportResult> SelectPagedAsync(IEntitySerializer serializer, FilterCondition[] conditions, bool any, int pageNumber, int pageSize,
        EntityRequestContext context, CancellationToken ct = default)
        => TransportResult.RunAsync(async () =>
        {
            using var relations = RelationReadGuard.Begin(new HandlerRelationGuard(context));
            var predicate = Predicate(conditions ?? [], any, context);
            CheckPage(pageNumber, Math.Min(pageSize, _maxPageSize));
            await AuthorizeAsync(EntityOperation.Read, null, context, ct);
            await using var store = _storeFactory();
            return SerializeMany(serializer,
                await store.SelectPagedAsync(Restrict(predicate, context), pageNumber, Math.Min(pageSize, _maxPageSize), ct));
        });

    public override Task<TransportResult> SelectPagedAsync(IEntitySerializer serializer, FilterCondition[] conditions, bool any, int pageNumber,
        int pageSize, SortKey[] sort, EntityRequestContext context, CancellationToken ct = default)
        => sort is not { Length: > 0 }
            ? SelectPagedAsync(serializer, conditions, any, pageNumber, pageSize, context, ct)
            : TransportResult.RunAsync(async () =>
            {
                using var relations = RelationReadGuard.Begin(new HandlerRelationGuard(context));
                var predicate = Predicate(conditions ?? [], any, context);
                var order = Sort<T>.FromKeys(sort, RemoteMembers<T>.Queryable);       // only queryable properties of T, at most Sort<T>.MaxKeys
                CheckPage(pageNumber, Math.Min(pageSize, _maxPageSize));
                await AuthorizeAsync(EntityOperation.Read, null, context, ct);
                await using var store = _storeFactory();
                var size = Math.Min(pageSize, _maxPageSize);
                if (store.SortsInStore)
                    return SerializeMany(serializer, await store.SelectPagedAsync(Restrict(predicate, context), pageNumber, size, order, ct));

                // A store that sorts in memory reads every matching row: at most maxReadSize of them, as any read of every
                // row, so that "page 1 of 1, sorted" never loads a whole table.
                var rows = await ReadBoundedAsync(store, Restrict(predicate, context), ct);
                return SerializeMany(serializer, order.Apply(rows).Skip((pageNumber - 1) * size).Take(size).ToList());
            });

    public override Task<TransportResult> GetChangesAsync(IEntitySerializer serializer, DateTime sinceUtc, EntityRequestContext context, CancellationToken ct = default)
        => TransportResult.RunAsync(async () =>
        {
            await AuthorizeAsync(EntityOperation.Read, null, context, ct);
            await using var store = _storeFactory();

            // The user's rows only, and one more than allowed at most, both in the query: a date far in the past or a large
            // table costs this user's rows, never the whole table filtered afterwards.
            var changes = await store.SelectChangesAsync(sinceUtc, Restrict(All, context), _maxReadSize + 1, ct);
            return changes.Count > _maxReadSize ? throw TooMany() : SerializeMany(serializer, changes);
        });

    // ---------- Write ----------

    public override Task<TransportResult> WriteAsync(IEntitySerializer serializer, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, CancellationToken ct = default)
        => TransportResult.RunAsync(async () =>
        {
            if (aggregate is not null)
                return await WriteThroughAggregateAsync(serializer, aggregate, kind, entity, context, ct);

            await using var store = _storeFactory();
            if (rows is null && RemoteMembers<T>.ServerOwned.Count == 0 && OmittedBy(serializer).Length == 0)
                return await WriteAsync(serializer, store, kind, entity, context, requireEffect: false, ct);

            // What is checked or kept from the stored row (its owner, its [ServerOwned] values) and the write itself, in one
            // transaction: with SQLite or under SERIALIZABLE the row cannot change in between. Under READ COMMITTED
            // (PostgreSQL, SQL Server), a version ([ConcurrencyCheck]) on the entity turns a concurrent change into a Conflict.
            await using var transaction = await store.BeginTransactionAsync(ct);
            var written = await WriteAsync(serializer, store, kind, entity, context, requireEffect: false, ct);
            (await transaction.CommitAsync(ct)).ThrowIfFailed();
            return written;
        });

    public override async Task<byte[]> WriteAsync(IEntitySerializer serializer, IEntityStore store, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, bool requireEffect, CancellationToken ct = default)
    {
        if (aggregate is not null and not IGraphRepository<T>)
            throw new EntityStoreException(EntityErrorCode.NotSupported,
                $"{typeof(T).Name} is saved by its own transactional service: it cannot be part of a batch.");

        var value = await PrepareAsync(serializer, kind, entity, context, ct);
        await CheckRowsAsync(store, kind, value, context, ct);
        await KeepStoredAsync(serializer, store, kind, value, ct);

        // A graph: the parent and its children, in the batch's transaction.
        if (aggregate is IGraphRepository<T> { Graph: var graph })
        {
            using (ChangeContext.Begin(context.ConnectionId))
            using (GraphWriteGuard.Begin(new HandlerGraphGuard(serializer, context)))
            {
                if (kind == BatchOperationKind.Delete)
                {
                    await graph.RemoveAsync(store, value, ct);
                    return serializer.Serialize(true);
                }

                await graph.SaveAsync(store, value, ct);
                return serializer.Serialize(value); // generated keys, also those of the children
            }
        }

        // The client that initiated the change is passed on to the notification source.
        using (ChangeContext.Begin(context.ConnectionId))
        {
            switch (kind)
            {
                case BatchOperationKind.Insert:
                    await store.InsertAsync(value, ct);
                    return serializer.Serialize(value); // generated key and version

                case BatchOperationKind.Update:
                    if (await store.UpdateAsync(value, ct))
                        return serializer.Serialize(value); // new version
                    return requireEffect ? throw NotFound() : [];

                default:
                    var deleted = await store.DeleteAsync(value, ct);
                    if (!deleted && requireEffect)
                        throw NotFound();
                    return serializer.Serialize(deleted);
            }
        }
    }

    private async Task<byte[]> WriteThroughAggregateAsync(IEntitySerializer serializer, IAggregateRepository<T> service, BatchOperationKind kind, byte[] entity,
        EntityRequestContext context, CancellationToken ct)
    {
        var value = await PrepareAsync(serializer, kind, entity, context, ct);
        if (rows is not null || RemoteMembers<T>.ServerOwned.Count > 0 || OmittedBy(serializer).Length > 0)
        {
            await using var store = _storeFactory();
            await CheckRowsAsync(store, kind, value, context, ct);
            await KeepStoredAsync(serializer, store, kind, value, ct);
        }

        // A graph repository saves the children too: each follows the rules of its own type (no effect on another service).
        using (ChangeContext.Begin(context.ConnectionId))
        using (GraphWriteGuard.Begin(new HandlerGraphGuard(serializer, context)))
        {
            if (kind == BatchOperationKind.Delete)
            {
                (await service.RemoveAsync(value, ct)).ThrowIfFailed();
                return serializer.Serialize(true);
            }

            (await service.SaveAsync(value, ct)).ThrowIfFailed();
            return serializer.Serialize(value); // generated keys, also those of the children
        }
    }

    /// <summary>Deserializes the entity, checks the access policy and validates it for insert and update.</summary>
    private async Task<T> PrepareAsync(IEntitySerializer serializer, BatchOperationKind kind, byte[] entity, EntityRequestContext context, CancellationToken ct)
    {
        var value = serializer.Deserialize<T>(entity) ?? throw new ArgumentException($"Empty {typeof(T).Name} entity.");

        // What the server owns never comes from the client: discarded on insert, taken from the stored row on update.
        if (kind == BatchOperationKind.Insert)
        {
            RemoteMembers<T>.DiscardClientValues(value);
            RemoteMembers<T>.DiscardClientKey(value);
        }

        var operation = kind switch
        {
            BatchOperationKind.Insert => EntityOperation.Insert,
            BatchOperationKind.Update => EntityOperation.Update,
            BatchOperationKind.Delete => EntityOperation.Delete,
            _ => throw new ArgumentException($"Unknown operation: {kind}.")
        };
        await AuthorizeAsync(operation, value, context, ct);

        if (validate && kind is BatchOperationKind.Insert or BatchOperationKind.Update)
        {
            // The children of a graph too: a valid parent must not carry invalid children into the database.
            var errors = EntityValidator.ValidateGraph(value).Select(e => e.ErrorMessage ?? "Invalid value.").ToList();
            if (errors.Count > 0)
                throw new EntityStoreException(EntityErrorCode.Validation,
                    $"Invalid {typeof(T).Name}: {string.Join(" ", errors)}", errors);
        }

        return value;
    }

    // ---------- Requests beyond rows (RemoteRequest) ----------

    /// <summary>The most names a request may carry (projected properties, children left unchanged).</summary>
    public const int MaxNames = 10_000;

    public override Task<TransportResult> ExecuteAsync(IEntitySerializer serializer, RemoteRequest request, EntityRequestContext context,
        CancellationToken ct = default)
        => TransportResult.RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Members.Length > MaxNames)
                throw new ArgumentException($"A request names at most {MaxNames} members.");
            using var relations = RelationReadGuard.Begin(new HandlerRelationGuard(context));
            return request.Kind switch
            {
                RemoteRequestKind.Count => await CountAsync(serializer, request, context, ct),
                RemoteRequestKind.Aggregate => await AggregateAsync(serializer, request, context, ct),
                RemoteRequestKind.Project => await ProjectAsync(serializer, request, context, ct),
                RemoteRequestKind.Patch => await PatchAsync(serializer, request, context, ct),
                RemoteRequestKind.UpdateWhere or RemoteRequestKind.DeleteWhere => await WriteWhereAsync(serializer, request, context, ct),
                RemoteRequestKind.SaveGraph => await SaveGraphAsync(serializer, request, context, ct),
                _ => throw new ArgumentException($"Unknown request: {request.Kind}."),
            };
        });

    private async Task<byte[]?> CountAsync(IEntitySerializer serializer, RemoteRequest request, EntityRequestContext context, CancellationToken ct)
    {
        var predicate = Predicate(request.Filter ?? [], request.Any, context);
        await AuthorizeAsync(EntityOperation.Read, null, context, ct);
        await using var store = _storeFactory();
        return serializer.Serialize(await store.CountAsync(Restrict(predicate, context), ct));
    }

    private async Task<byte[]?> AggregateAsync(IEntitySerializer serializer, RemoteRequest request, EntityRequestContext context, CancellationToken ct)
    {
        var predicate = Predicate(request.Filter ?? [], request.Any, context);
        var parameter = Expression.Parameter(typeof(T), "e");
        var (key, values) = FilterLanguage.BindAggregation(request.Values ?? [], FilterScope(context.Handlers), parameter);
        var aggregation = Aggregation.Of<T>();
        if (key is not null)
            aggregation.GroupBy(Expression.Lambda<Func<T, object?>>(Expression.Convert(key, typeof(object)), parameter));
        foreach (var (function, value) in values)
        {
            aggregation.Add(function, value is null ? null : Expression.Lambda<Func<T, object?>>(Expression.Convert(value, typeof(object)), parameter),
                value?.Type);
        }
        await AuthorizeAsync(EntityOperation.Read, null, context, ct);
        await using var store = _storeFactory();
        var rows = await store.AggregateAsync(Restrict(predicate, context), aggregation, ct);
        if (rows.Count > _maxReadSize)
            throw TooMany();
        return serializer.Serialize(rows
            .Select(r => new object?[] { FilterLanguage.Normalize(r.Key), r.Count }.Concat(r.Values.Select(FilterLanguage.Normalize)).ToArray())
            .ToArray());
    }

    /// <summary>The matching rows with only the asked properties: the others are left at their default, and cost nothing to send.</summary>
    private async Task<byte[]?> ProjectAsync(IEntitySerializer serializer, RemoteRequest request, EntityRequestContext context, CancellationToken ct)
    {
        var kept = request.Members.ToHashSet(StringComparer.Ordinal);
        var unknown = kept.Where(m => EntityValues.Columns<T>().All(c => c.Name != m)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"Unknown property '{unknown[0]}' on {typeof(T).Name}.");
        var predicate = Predicate(request.Filter ?? [], request.Any, context);
        await AuthorizeAsync(EntityOperation.Read, null, context, ct);
        await using var store = _storeFactory();
        var rows = await ReadBoundedAsync(store, Restrict(predicate, context), ct);
        var cleared = EntityValues.Columns<T>().Where(c => !kept.Contains(c.Name)).ToList();
        foreach (var row in rows)
        {
            foreach (var column in cleared)
                column.SetValue(row, column.PropertyType.IsValueType && Nullable.GetUnderlyingType(column.PropertyType) is null ? Defaults.Of(column.PropertyType) : null);
        }
        return SerializeMany(serializer, rows);
    }

    /// <summary>
    /// The properties one client changed, written over the stored row: what the server owns, the key, the version and what the
    /// serializer leaves out are never taken from it.
    /// </summary>
    private async Task<byte[]?> PatchAsync(IEntitySerializer serializer, RemoteRequest request, EntityRequestContext context, CancellationToken ct)
    {
        var original = serializer.Deserialize<T>(request.Original ?? []) ?? throw new ArgumentException($"Empty {typeof(T).Name} original.");
        var modified = serializer.Deserialize<T>(request.Entity ?? []) ?? throw new ArgumentException($"Empty {typeof(T).Name} entity.");
        var columns = Patchable(serializer, request.Members);
        await AuthorizeAsync(EntityOperation.Update, modified, context, ct);

        await using var store = _storeFactory();
        await using var transaction = await store.BeginTransactionAsync(ct);
        var stored = await store.FirstOrDefaultAsync(Restrict(SyncMetadata.SameKey(modified), context), ct);
        if (stored is null)
            return [];
        if (EntityPatch.Merge(stored, original, modified, columns))
        {
            Validate(stored);
            CheckVisible(stored, context);
            using (ChangeContext.Begin(context.ConnectionId))
            {
                if (!await store.UpdateAsync(stored, ct))
                    return [];
            }
        }
        (await transaction.CommitAsync(ct)).ThrowIfFailed();
        return serializer.Serialize(stored);
    }

    /// <summary>Every matching row updated (the values of the request) or deleted, in one transaction, each checked as a write of its own.</summary>
    private async Task<byte[]?> WriteWhereAsync(IEntitySerializer serializer, RemoteRequest request, EntityRequestContext context, CancellationToken ct)
    {
        var delete = request.Kind == RemoteRequestKind.DeleteWhere;
        var assignments = delete ? [] : Assignments(serializer, request.Values ?? []);
        if (!delete && assignments.Count == 0)
            throw new ArgumentException("Nothing to set.");
        var predicate = Predicate(request.Filter ?? [], request.Any, context);
        var operation = delete ? EntityOperation.Delete : EntityOperation.Update;
        await AuthorizeAsync(operation, null, context, ct);

        await using var store = _storeFactory();
        await using var transaction = await store.BeginTransactionAsync(ct);
        var rows = await ReadBoundedAsync(store, Restrict(predicate, context), ct);
        var count = 0;
        using (ChangeContext.Begin(context.ConnectionId))
        {
            foreach (var row in rows)
            {
                if (delete)
                {
                    await AuthorizeAsync(operation, row, context, ct);
                    if (await store.DeleteAsync(row, ct))
                        count++;
                    continue;
                }
                foreach (var (property, value) in assignments)
                    property.SetValue(row, value);
                await AuthorizeAsync(operation, row, context, ct);
                Validate(row);
                CheckVisible(row, context);
                if (!await store.UpdateAsync(row, ct))
                    throw NotFound();
                count++;
            }
        }
        (await transaction.CommitAsync(ct)).ThrowIfFailed();
        return serializer.Serialize(count);
    }

    /// <summary>An update of a graph, the children the client left as read named: their rows are left as they are.</summary>
    private async Task<byte[]?> SaveGraphAsync(IEntitySerializer serializer, RemoteRequest request, EntityRequestContext context, CancellationToken ct)
    {
        var entity = request.Entity ?? throw new ArgumentException($"Empty {typeof(T).Name} entity.");
        using (GraphChanges.Begin(request.Members.ToHashSet(StringComparer.Ordinal)))
            return (await WriteAsync(serializer, BatchOperationKind.Update, entity, context, ct)).GetDataOrThrow();
    }

    /// <summary>The columns a client may write in a patch or an update of many rows.</summary>
    private static System.Reflection.PropertyInfo[] Patchable(IEntitySerializer serializer, IEnumerable<string> names)
    {
        var columns = new List<System.Reflection.PropertyInfo>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var column = EntityValues.Writable<T>().FirstOrDefault(c => c.Name == name)
                ?? throw new ArgumentException($"Unknown property '{name}' on {typeof(T).Name}.");
            if (RemoteMembers<T>.ServerOwned.Contains(column) || OmittedBy(serializer).Contains(column))
                throw new EntityStoreException(EntityErrorCode.Forbidden, $"{typeof(T).Name}.{name} is not written by clients.");
            columns.Add(column);
        }
        return [.. columns];
    }

    private static List<(System.Reflection.PropertyInfo Property, object? Value)> Assignments(IEntitySerializer serializer, IEnumerable<FilterCondition> values)
    {
        var assignments = new List<(System.Reflection.PropertyInfo, object?)>();
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            var column = Patchable(serializer, [value.Property])[0];
            assignments.Add((column, Filters.ConvertTo(value.Value, column.PropertyType, value.Property)));
        }
        return assignments;
    }

    private void Validate(T value)
    {
        if (!validate)
            return;
        var errors = EntityValidator.ValidateGraph(value).Select(e => e.ErrorMessage ?? "Invalid value.").ToList();
        if (errors.Count > 0)
            throw new EntityStoreException(EntityErrorCode.Validation, $"Invalid {typeof(T).Name}: {string.Join(" ", errors)}", errors);
    }

    /// <summary>A row written by a client must stay among the rows it may write.</summary>
    private void CheckVisible(T value, EntityRequestContext context)
    {
        if (rows is not null && !rows.Rows(context.User).Compile(preferInterpretation: true)(value))
            throw new EntityStoreException(EntityErrorCode.Forbidden, $"Access denied: this {typeof(T).Name} is outside the rows you may write.");
    }

    // ---------- Filters ----------

    private Expression<Func<T, bool>> Predicate(FilterCondition[] conditions, bool any, EntityRequestContext context)
        => Filters.ToPredicate<T>(conditions, any, RemoteMembers<T>.Queryable, FilterScope(context.Handlers));

    internal override IFilterScope FilterScope(IEntityHandlerRegistry? registry) => new Scope(registry);

    internal override bool IsCollectionOf(Type type) => typeof(IEnumerable<T>).IsAssignableFrom(type) && type != typeof(string);

    /// <summary>The related rows a filter of another type reads: those of this type the user may read.</summary>
    internal async Task<Expression<Func<T, bool>>> RestrictRelationAsync(Expression<Func<T, bool>> predicate, EntityRequestContext context,
        CancellationToken ct)
    {
        await AuthorizeAsync(EntityOperation.Read, null, context, ct);
        return Restrict(predicate, context);
    }

    /// <summary>What a filter on <typeparamref name="T"/> may name: its queryable columns, and navigations toward exposed types.</summary>
    private sealed class Scope(IEntityHandlerRegistry? registry) : IFilterScope
    {
        private static readonly System.Reflection.MethodInfo AnyAll = new Func<IEnumerable<T>, bool>(Enumerable.Any).Method;
        private static readonly System.Reflection.MethodInfo AnyWhere = new Func<IEnumerable<T>, Func<T, bool>, bool>(Enumerable.Any).Method;
        private static readonly System.Reflection.MethodInfo AllWhere = new Func<IEnumerable<T>, Func<T, bool>, bool>(Enumerable.All).Method;
        private static readonly System.Reflection.MethodInfo CountAll = new Func<IEnumerable<T>, int>(Enumerable.Count).Method;
        private static readonly System.Reflection.MethodInfo CountWhere = new Func<IEnumerable<T>, Func<T, bool>, int>(Enumerable.Count).Method;

        public Type Type => typeof(T);

        public ParameterExpression NewParameter() => Expression.Parameter(typeof(T), typeof(T).Name[..1].ToLowerInvariant());

        public System.Reflection.PropertyInfo? Column(string name)
            => RemoteMembers<T>.Queryable.Contains(name) && typeof(T).GetProperty(name) is { } property && EntityValues.IsSimple(property.PropertyType)
                ? property
                : null;

        public (System.Reflection.PropertyInfo Property, IFilterScope Scope, bool Collection)? Navigation(string name)
        {
            if (registry is null || !RemoteMembers<T>.Queryable.Contains(name) || typeof(T).GetProperty(name) is not { } property
                || EntityValues.IsSimple(property.PropertyType))
                return null;
            var type = property.PropertyType;
            var element = type.IsArray ? type.GetElementType() : type.IsGenericType && type.GenericTypeArguments.Length == 1 ? type.GenericTypeArguments[0] : null;
            if (element is not null && registry.TryGet(EntityName.Of(element), out var items) && items.Type == element && items.IsCollectionOf(type))
                return (property, items.FilterScope(registry), true);
            if (registry.TryGet(EntityName.Of(type), out var target) && target.Type == type)
                return (property, target.FilterScope(registry), false);
            return null;
        }

        public LambdaExpression Lambda(Expression body, ParameterExpression parameter) => Expression.Lambda<Func<T, bool>>(body, parameter);

        public Expression Any(Expression source, LambdaExpression? predicate)
            => predicate is null ? Expression.Call(AnyAll, source) : Expression.Call(AnyWhere, source, predicate);

        public Expression All(Expression source, LambdaExpression predicate) => Expression.Call(AllWhere, source, predicate);

        public Expression Count(Expression source, LambdaExpression? predicate)
            => predicate is null ? Expression.Call(CountAll, source) : Expression.Call(CountWhere, source, predicate);
    }

    // ---------- Internals ----------

    /// <summary>
    /// On an update, what the client does not decide takes the stored row's values: the [ServerOwned] properties, whatever
    /// the client sent, and the properties the serializer leaves out (a password hash), which the client never had. Every
    /// store writes the whole row: without this, an update would erase them.
    /// </summary>
    private static async Task KeepStoredAsync(IEntitySerializer serializer, IEntityStore store, BatchOperationKind kind, T value, CancellationToken ct)
    {
        var omitted = OmittedBy(serializer);
        if (kind != BatchOperationKind.Update || RemoteMembers<T>.ServerOwned.Count == 0 && omitted.Length == 0)
            return;
        if (SyncMetadata.Key(typeof(T)) is not { } key)
            throw new EntityStoreException(EntityErrorCode.NotSupported,
                $"{typeof(T).Name} has properties the server keeps ([ServerOwned], or left out by the serializer) but no key "
                + "([Key] or Id) to read its stored row with.");

        var parameter = Expression.Parameter(typeof(T), "e");
        var sameKey = Expression.Lambda<Func<T, bool>>(
            Expression.Equal(Expression.Property(parameter, key), Expression.Constant(key.GetValue(value), key.PropertyType)), parameter);
        if (await store.FirstOrDefaultAsync(sameKey, ct) is { } stored)
        {
            RemoteMembers<T>.KeepServerValues(value, stored);
            RemoteMembers<T>.KeepValues(value, stored, omitted);
        }
    }

    /// <summary>Deepest page a client may ask for, in rows skipped: beyond, the database would read them all to skip them.</summary>
    public const int MaxOffset = 1_000_000;

    private static void CheckPage(int pageNumber, int pageSize)
    {
        Paging.Validate(pageNumber, pageSize);
        if ((long)(pageNumber - 1) * pageSize > MaxOffset)
            throw new ArgumentOutOfRangeException(nameof(pageNumber),
                $"The page starts beyond {MaxOffset} rows: narrow the filter, or sort and filter from the last row seen.");
    }

    private Expression<Func<T, bool>> Restrict(Expression<Func<T, bool>> predicate, EntityRequestContext context)
        => rows is null ? predicate : rows.Restrict(predicate, context.User);

    /// <summary>At most <c>maxReadSize</c> rows, read in one query: one more is enough to know there are too many.</summary>
    private async Task<IReadOnlyList<T>> ReadBoundedAsync(IEntityStore store, Expression<Func<T, bool>> predicate, CancellationToken ct)
    {
        var found = await store.SelectPagedAsync(predicate, 1, _maxReadSize + 1, ct);
        return found.Count > _maxReadSize ? throw TooMany() : found;
    }

    private EntityStoreException TooMany()
        => new(EntityErrorCode.InvalidRequest,
            $"More than {_maxReadSize} {typeof(T).Name} rows match: read them by page, or narrow the filter.");

    /// <summary>
    /// A child of a graph sent by a client, checked as if it were written on its own: what the server owns or the
    /// serializer leaves out comes from the stored row (reset on an insert), the access policy of the child's type, and its
    /// row filter (the stored row and the new content must both be the user's).
    /// </summary>
    internal async Task GuardChildAsync(GraphWrite write, T child, T? stored, IEntitySerializer serializer, EntityRequestContext context,
        CancellationToken ct)
    {
        HandlerGraphGuard.KeepServerValues(write, child, stored, serializer);
        await AuthorizeAsync(write switch
        {
            GraphWrite.Insert => EntityOperation.Insert,
            GraphWrite.Update => EntityOperation.Update,
            _ => EntityOperation.Delete,
        }, child, context, ct);
        if (rows is null)
            return;
        var visible = rows.Rows(context.User).Compile(preferInterpretation: true);
        if (write != GraphWrite.Insert && (stored is null || !visible(stored)))
            throw NotFound();
        if (write != GraphWrite.Delete && !visible(child))
            throw new EntityStoreException(EntityErrorCode.Forbidden,
                $"Access denied: this {typeof(T).Name} is outside the rows you may write.");
    }

    /// <summary>
    /// With a row filter: the written entity must be one of the user's rows, and so must the stored row an update or a
    /// delete targets. A row of someone else answers NotFound, as if it did not exist.
    /// </summary>
    private async Task CheckRowsAsync(IEntityStore store, BatchOperationKind kind, T value, EntityRequestContext context, CancellationToken ct)
    {
        if (rows is null)
            return;

        // The stored row first: someone else's row answers NotFound, whatever the new content says.
        var visible = rows.Rows(context.User);
        if (kind != BatchOperationKind.Insert
            && await store.FirstOrDefaultAsync(RowLevelSecurity<T>.And(rows.SameKey(value), visible), ct) is null)
            throw NotFound();

        // One evaluation: interpreting the expression costs less than compiling it (and Native AOT interprets anyway).
        if (kind != BatchOperationKind.Delete && !visible.Compile(preferInterpretation: true)(value))
            throw new EntityStoreException(EntityErrorCode.Forbidden,
                $"Access denied: this {typeof(T).Name} is outside the rows you may write.");
    }

    private async Task AuthorizeAsync(EntityOperation operation, T? entity, EntityRequestContext context, CancellationToken ct)
    {
        if (!await _policy.IsAllowedAsync(new EntityAccessRequest(operation, typeof(T), entity, context.User), ct))
            throw new EntityStoreException(EntityErrorCode.Forbidden, $"Access denied: {operation} on {typeof(T).Name}.");
    }

    private static EntityStoreException NotFound()
        => new(EntityErrorCode.NotFound, $"{typeof(T).Name} not found: the row no longer exists.");

    private static byte[] SerializeMany(IEntitySerializer serializer, IReadOnlyList<T> entities)
        => serializer.Serialize(entities.ToArray());
}

/// <summary>
/// The children of a graph sent by a remote client follow the rules of their own type: those of its exposed handler
/// (access policy, row filter) when the type is exposed, and in any case the values the server keeps.
/// </summary>
/// <summary>
/// The rows a relation filter reads, on a server: the related type must be exposed and readable by the user, and only its rows
/// the user may read count. A filter cannot reveal through a relation what the user could not read directly.
/// </summary>
internal sealed class HandlerRelationGuard(EntityRequestContext context) : IRelationReadGuard
{
    public async Task<Expression<Func<TChild, bool>>> RestrictAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TChild>(
        Expression<Func<TChild, bool>> predicate, CancellationToken ct) where TChild : class
        => context.Handlers is { } handlers && handlers.TryGet(EntityName.Of<TChild>(), out var handler) && handler is EntityHandler<TChild> typed
            ? await typed.RestrictRelationAsync(predicate, context, ct).ConfigureAwait(false)
            : throw new EntityStoreException(EntityErrorCode.Forbidden, $"{typeof(TChild).Name} is not exposed: a filter cannot go through it.");
}

/// <summary>Default values of value types, without reflection on the type (a projection clears the properties not asked for).</summary>
internal static class Defaults
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "A value type of a column of an exposed entity: its boxed default exists.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "A value type of a column of an exposed entity.")]
    public static object? Of(Type type) => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
}

internal sealed class HandlerGraphGuard(IEntitySerializer serializer, EntityRequestContext context) : IGraphWriteGuard
{
    public async Task CheckAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TChild>(IEntityStore store, GraphWrite write, TChild child,
        TChild? stored, CancellationToken ct) where TChild : class
    {
        if (context.Handlers is { } handlers && handlers.TryGet(EntityName.Of<TChild>(), out var handler) && handler is EntityHandler<TChild> typed)
            await typed.GuardChildAsync(write, child, stored, serializer, context, ct);
        else
            KeepServerValues(write, child, stored, serializer);
    }

    /// <summary>[ServerOwned] reset on an insert; on an update, it and what the serializer leaves out come from the stored row.</summary>
    internal static void KeepServerValues<[DynamicallyAccessedMembers(EntityMembers.Required)] TChild>(GraphWrite write, TChild child, TChild? stored,
        IEntitySerializer serializer) where TChild : class
    {
        if (write == GraphWrite.Insert)
        {
            RemoteMembers<TChild>.DiscardClientValues(child);
        }
        else if (write == GraphWrite.Update && stored is not null)
        {
            RemoteMembers<TChild>.KeepServerValues(child, stored);
            RemoteMembers<TChild>.KeepValues(child, stored, RemoteMembers<TChild>.MarkedHidden.Where(serializer.Omits));
        }
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables.Abstraction;

/// <summary>
/// A query composed in the LINQ style: <c>Where</c>, <c>OrderBy</c>, <c>Skip</c>, <c>Take</c>, <c>Select</c>, then
/// <c>ToListAsync</c>, <c>FirstOrDefaultAsync</c>, <c>CountAsync</c> or <c>AnyAsync</c>. It runs on any store, through the
/// <see cref="IEntityStore"/> methods: filtered, sorted and paged by the database with EF Core and by the server remotely.
/// Immutable: each method returns a new query, so a base query can be shared.
/// </summary>
/// <remarks>
/// Not an <see cref="IQueryable{T}"/>: only these operators exist, so a query that a store could not run does not compile.
/// Joins, grouping and subqueries stay with the ORM.
/// </remarks>
/// <example>
/// <code>
/// var rows = await stores.Query&lt;Person&gt;()
///     .Where(p => p.Active)
///     .Where(p => p.Name.StartsWith(search))
///     .OrderBy(p => p.LastName).ThenByDescending(p => p.Id)
///     .Skip(50).Take(25)
///     .Select(p => new PersonRow(p.Id, p.Name))
///     .ToListAsync();
/// </code>
/// </example>
public sealed class EntityQuery<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity> where TEntity : class
{
    private readonly QuerySource _source;

    internal EntityQuery(QuerySource source, Expression<Func<TEntity, bool>>? predicate = null, Sort<TEntity>? sort = null,
        int skip = 0, int? take = null)
    {
        _source = source;
        Predicate = predicate;
        Sort = sort;
        SkipCount = skip;
        TakeCount = take;
    }

    /// <summary>The conditions, joined with AND; null for every row.</summary>
    public Expression<Func<TEntity, bool>>? Predicate { get; }

    public Sort<TEntity>? Sort { get; }

    public int SkipCount { get; }

    public int? TakeCount { get; }

    // ---------- Composition ----------

    /// <summary>Adds a condition; several <c>Where</c> are joined with AND.</summary>
    public EntityQuery<TEntity> Where(Expression<Func<TEntity, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return With(predicate: Predicate is null ? predicate : And(Predicate, predicate));
    }

    public EntityQuery<TEntity> OrderBy<TValue>(Expression<Func<TEntity, TValue>> property)
        => With(sort: Sort<TEntity>.By(property));

    public EntityQuery<TEntity> OrderByDescending<TValue>(Expression<Func<TEntity, TValue>> property)
        => With(sort: Sort<TEntity>.ByDescending(property));

    public EntityQuery<TEntity> ThenBy<TValue>(Expression<Func<TEntity, TValue>> property)
        => With(sort: (Sort ?? throw OrderFirst()).ThenBy(property));

    public EntityQuery<TEntity> ThenByDescending<TValue>(Expression<Func<TEntity, TValue>> property)
        => With(sort: (Sort ?? throw OrderFirst()).ThenByDescending(property));

    /// <summary>Skips rows; adds to a previous <c>Skip</c>, and shortens a previous <c>Take</c>, like LINQ.</summary>
    public EntityQuery<TEntity> Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return new EntityQuery<TEntity>(_source, Predicate, Sort, SkipCount + count,
            TakeCount is { } take ? Math.Max(0, take - count) : null);
    }

    /// <summary>At most <paramref name="count"/> rows; keeps the smaller of two <c>Take</c>, like LINQ.</summary>
    public EntityQuery<TEntity> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return new EntityQuery<TEntity>(_source, Predicate, Sort, SkipCount, TakeCount is { } take ? Math.Min(take, count) : count);
    }

    /// <summary>Page <paramref name="pageNumber"/> (starting at 1) of <paramref name="pageSize"/> rows.</summary>
    public EntityQuery<TEntity> Page(int pageNumber, int pageSize)
    {
        Paging.Validate(pageNumber, pageSize);
        return Skip((pageNumber - 1) * pageSize).Take(pageSize);
    }

    /// <summary>
    /// Projects the rows. EF Core projects in the database when the query is neither sorted nor paged; otherwise the
    /// entities are read, then projected.
    /// </summary>
    public ProjectedQuery<TEntity, TResult> Select<TResult>(Expression<Func<TEntity, TResult>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new ProjectedQuery<TEntity, TResult>(this, selector);
    }

    // ---------- Execution ----------

    public Task<IReadOnlyList<TEntity>> ToListAsync(CancellationToken ct = default) => _source.RunAsync(store => ReadAsync(store, ct));

    public Task<TEntity?> FirstOrDefaultAsync(CancellationToken ct = default)
        => _source.RunAsync(async store => (await Take(1).ReadAsync(store, ct)).FirstOrDefault());

    /// <summary>The first row matching, or <see cref="EntityStoreException"/> (NotFound).</summary>
    public async Task<TEntity> FirstAsync(CancellationToken ct = default)
        => await FirstOrDefaultAsync(ct)
           ?? throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TEntity).Name} not found.");

    /// <summary>The number of rows matching, counted by the store; <c>Skip</c> and <c>Take</c> apply to it, like LINQ.</summary>
    public Task<long> CountAsync(CancellationToken ct = default)
        => _source.RunAsync(async store =>
        {
            var count = await store.CountAsync(Predicate ?? All, ct);
            var remaining = Math.Max(0, count - SkipCount);
            return TakeCount is { } take ? Math.Min(remaining, take) : remaining;
        });

    public async Task<bool> AnyAsync(CancellationToken ct = default) => await Take(1).ToListAsync(ct) is { Count: > 0 };

    /// <summary>Reads the rows on <paramref name="store"/>: one call, or two when <c>Skip</c> is not a multiple of <c>Take</c>.</summary>
    internal async Task<IReadOnlyList<TEntity>> ReadAsync(IEntityStore store, CancellationToken ct)
    {
        var predicate = Predicate ?? All;

        if (TakeCount is 0)
            return [];

        // Not paged: every matching row, then the skip.
        if (TakeCount is not { } take)
        {
            var rows = Sort is null
                ? Predicate is null ? await store.GetAllAsync<TEntity>(ct) : await store.SelectAsync(predicate, ct)
                : await store.SelectAsync(predicate, Sort, ct);
            return SkipCount == 0 ? rows : rows.Skip(SkipCount).ToList();
        }

        // Paged: stores page by number. A skip that is a multiple of the size is one page; otherwise the two pages around it.
        var page = SkipCount / take + 1;
        var offset = SkipCount % take;
        var first = await PageAsync(store, predicate, page, take, ct);
        if (offset == 0)
            return first;

        var rowsFound = first.Skip(offset).ToList();
        if (first.Count == take)
            rowsFound.AddRange((await PageAsync(store, predicate, page + 1, take, ct)).Take(take - rowsFound.Count));
        return rowsFound;
    }

    private Task<IReadOnlyList<TEntity>> PageAsync(IEntityStore store, Expression<Func<TEntity, bool>> predicate, int page, int size,
        CancellationToken ct)
        => Sort is null
            ? store.SelectPagedAsync(predicate, page, size, ct)
            : store.SelectPagedAsync(predicate, page, size, Sort, ct);

    internal QuerySource Source => _source;

    private static readonly Expression<Func<TEntity, bool>> All = _ => true;

    private EntityQuery<TEntity> With(Expression<Func<TEntity, bool>>? predicate = null, Sort<TEntity>? sort = null)
        => new(_source, predicate ?? Predicate, sort ?? Sort, SkipCount, TakeCount);

    private static InvalidOperationException OrderFirst() => new("Call OrderBy or OrderByDescending before ThenBy.");

    /// <summary><c>a &amp;&amp; b</c>, with the parameter of <paramref name="left"/> in both: the form every store translates.</summary>
    private static Expression<Func<TEntity, bool>> And(Expression<Func<TEntity, bool>> left, Expression<Func<TEntity, bool>> right)
    {
        var parameter = left.Parameters[0];
        var rightBody = new ParameterReplacer(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<TEntity, bool>>(Expression.AndAlso(left.Body, rightBody), parameter);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}

/// <summary>An <see cref="EntityQuery{TEntity}"/> followed by a projection.</summary>
public sealed class ProjectedQuery<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TResult> where TEntity : class
{
    private readonly EntityQuery<TEntity> _query;
    private readonly Expression<Func<TEntity, TResult>> _selector;
    private Func<TEntity, TResult>? _compiled;

    internal ProjectedQuery(EntityQuery<TEntity> query, Expression<Func<TEntity, TResult>> selector)
    {
        _query = query;
        _selector = selector;
    }

    public Task<IReadOnlyList<TResult>> ToListAsync(CancellationToken ct = default)
        => _query.Source.RunAsync(store => ReadAsync(store, _query, ct));

    public Task<TResult?> FirstOrDefaultAsync(CancellationToken ct = default)
        => _query.Source.RunAsync(async store => (await ReadAsync(store, _query.Take(1), ct)) is { Count: > 0 } rows ? rows[0] : default);

    public Task<long> CountAsync(CancellationToken ct = default) => _query.CountAsync(ct);

    private async Task<IReadOnlyList<TResult>> ReadAsync(IEntityStore store, EntityQuery<TEntity> query, CancellationToken ct)
    {
        // Nothing but a filter: the store projects (EF Core in the database).
        if (query is { Sort: null, SkipCount: 0, TakeCount: null })
            return await store.SelectAsync(query.Predicate ?? (_ => true), _selector, ct);

        _compiled ??= _selector.Compile();
        return (await query.ReadAsync(store, ct)).Select(_compiled).ToList();
    }
}

/// <summary>Where a query gets its store: a given one (left open), or a new one from a factory (disposed after the call).</summary>
internal sealed class QuerySource
{
    private readonly IEntityStore? _store;
    private readonly IEntityStoreFactory? _factory;

    public QuerySource(IEntityStore store) => _store = store;

    public QuerySource(IEntityStoreFactory factory) => _factory = factory;

    public async Task<T> RunAsync<T>(Func<IEntityStore, Task<T>> work)
    {
        if (_store is not null)
            return await work(_store);

        await using var store = _factory!.Create();
        return await work(store);
    }
}

public static class EntityQueryExtensions
{
    /// <summary>A LINQ-style query on this store; the store stays open.</summary>
    public static EntityQuery<TEntity> Query<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStore store)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(store);
        return new EntityQuery<TEntity>(new QuerySource(store));
    }

    /// <summary>A LINQ-style query; each execution creates a store and disposes it.</summary>
    public static EntityQuery<TEntity> Query<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(this IEntityStoreFactory stores)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(stores);
        return new EntityQuery<TEntity>(new QuerySource(stores));
    }
}

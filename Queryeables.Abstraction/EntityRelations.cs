using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace Queryeables.Abstraction;

/// <summary>
/// The graphs of the entity types, known to every store: a filter that goes through a relation
/// (<c>o =&gt; o.Lines.Any(l =&gt; l.ProductId == id)</c>, <c>o =&gt; o.Customer!.City == "Paris"</c>) is turned by the store
/// into queries it can run, from the graph of the type. The source generator registers the graphs of <c>[EntityGraph]</c>
/// and <c>[EntityReference]</c>; a graph built by hand is registered by <c>AddGraph</c>, by a
/// <see cref="GraphRepository{TParent, TKey}"/>, or here.
/// </summary>
public static class EntityRelations
{
    private static readonly ConcurrentDictionary<Type, Lazy<object>> Graphs = new();

    /// <summary>
    /// The largest number of keys a relation filter may turn into (<c>key in (...)</c>): beyond, the filter fails with a
    /// message rather than send a query a database refuses (SQL Server accepts 2100 parameters).
    /// </summary>
    public static int MaxKeys { get; set; } = 2000;

    /// <summary>
    /// The largest number of related rows a relation filter reads (one column of each, after counting them): beyond, it fails
    /// with a message rather than load a table. A server stays bounded whatever the filter a client sends.
    /// </summary>
    public static int MaxRows { get; set; } = 100_000;

    /// <summary>Registers the graph of <typeparamref name="TParent"/>, replacing the one known.</summary>
    public static void Register<[DynamicallyAccessedMembers(EntityMembers.Required)] TParent>(IEntityGraph<TParent> graph) where TParent : class
    {
        ArgumentNullException.ThrowIfNull(graph);
        Graphs[typeof(TParent)] = new Lazy<object>(graph);
    }

    /// <summary>Registers how to build the graph of <typeparamref name="TParent"/> when no graph is known for it yet.</summary>
    public static void TryRegister<[DynamicallyAccessedMembers(EntityMembers.Required)] TParent>(Func<IEntityGraph<TParent>> create) where TParent : class
    {
        ArgumentNullException.ThrowIfNull(create);
        Graphs.TryAdd(typeof(TParent), new Lazy<object>(() => create()));
    }

    /// <summary>The graph of <typeparamref name="TParent"/>; null when none was registered.</summary>
    public static IEntityGraph<TParent>? Find<[DynamicallyAccessedMembers(EntityMembers.Required)] TParent>() where TParent : class
        => Graphs.TryGetValue(typeof(TParent), out var graph) ? (IEntityGraph<TParent>)graph.Value : null;

    /// <summary>
    /// <paramref name="predicate"/> with its relation filters turned into conditions on columns, from queries on
    /// <paramref name="store"/>; the same predicate when it has none. Stores call it before translating a predicate.
    /// </summary>
    /// <exception cref="NotSupportedException">The predicate goes through a property that no registered graph declares.</exception>
    /// <param name="required">
    /// False for a store that translates navigations itself (EF Core): without a registered graph, the predicate is left as it is.
    /// </param>
    public static async Task<Expression<Func<T, bool>>> ExpandAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(
        IEntityStore store, Expression<Func<T, bool>> predicate, CancellationToken ct = default, bool required = true) where T : class
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(predicate);
        if (!UsesRelations(predicate))
            return predicate;
        var graph = Find<T>();
        if (graph is null && !required)
            return predicate;
        if (graph is null)
            throw new NotSupportedException(
            $"The filter on {typeof(T).Name} goes through a relation, but {typeof(T).Name} has no registered graph: mark the " +
            "relation with [EntityGraph] or [EntityReference], or register its graph (AddGraph, EntityRelations.Register).");
        return await graph.ExpandAsync(store, predicate, ct).ConfigureAwait(false);
    }

    /// <summary>True when <paramref name="predicate"/> reads a property of its parameter that is not a column (a navigation, a collection).</summary>
    public static bool UsesRelations(LambdaExpression predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var finder = new RelationFinder(predicate.Parameters[0]);
        finder.Visit(predicate.Body);
        return finder.Found;
    }

    private sealed class RelationFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == parameter && !EntityValues.IsSimple(node.Type))
                Found = true;
            return base.VisitMember(node);
        }
    }
}

/// <summary>
/// Restricts the rows a relation filter reads: on a server, the related type must be exposed, readable by the user, and
/// only its rows the user may read count (<c>RowLevelSecurity</c>). Without a guard (a local store), every row counts.
/// </summary>
public interface IRelationReadGuard
{
    /// <summary><paramref name="predicate"/> restricted to the rows of <typeparamref name="T"/> the user may read.</summary>
    /// <exception cref="EntityStoreException">The user may not read <typeparamref name="T"/> (Forbidden).</exception>
    Task<Expression<Func<T, bool>>> RestrictAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(
        Expression<Func<T, bool>> predicate, CancellationToken ct) where T : class;
}

/// <summary>The guard of the relation filters run in this async flow (set by a server for each request).</summary>
public static class RelationReadGuard
{
    private static readonly AsyncLocal<IRelationReadGuard?> Ambient = new();

    public static IRelationReadGuard? Current => Ambient.Value;

    public static IDisposable Begin(IRelationReadGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        var previous = Ambient.Value;
        Ambient.Value = guard;
        return new Scope(previous);
    }

    internal static async Task<Expression<Func<T, bool>>> RestrictAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(
        Expression<Func<T, bool>> predicate, CancellationToken ct) where T : class
        => Current is { } guard ? await guard.RestrictAsync(predicate, ct).ConfigureAwait(false) : predicate;

    private sealed class Scope(IRelationReadGuard? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

/// <summary>
/// The children a remote client left as it read them, sent with the save of their graph: the server leaves their rows as
/// they are, even when someone else changed them meanwhile (no conflict on lines this user did not touch).
/// </summary>
public static class GraphChanges
{
    private static readonly AsyncLocal<IReadOnlySet<string>?> Ambient = new();

    /// <summary>The ids (<see cref="IdOf"/>) of the unchanged children of the save running in this async flow.</summary>
    public static IReadOnlySet<string>? Unchanged => Ambient.Value;

    public static IDisposable Begin(IReadOnlySet<string> unchanged)
    {
        ArgumentNullException.ThrowIfNull(unchanged);
        var previous = Ambient.Value;
        Ambient.Value = unchanged;
        return new Scope(previous);
    }

    /// <summary>The id of a row: its type and its key (<c>Namespace.OrderLine|12</c>; the parts of a composite key, separated by commas).</summary>
    public static string IdOf(Type type, object key)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(key);
        var text = key is ITuple tuple
            ? string.Join(",", Enumerable.Range(0, tuple.Length).Select(i => tuple[i] is { } part ? EntityKey.Format(part) : ""))
            : EntityKey.Format(key);
        return $"{type.FullName}|{text}";
    }

    internal static bool Contains(Type type, object key) => Ambient.Value is { Count: > 0 } ids && ids.Contains(IdOf(type, key));

    private sealed class Scope(IReadOnlySet<string>? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

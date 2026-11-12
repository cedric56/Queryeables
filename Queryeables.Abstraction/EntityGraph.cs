using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Queryeables.Abstraction;

/// <summary>Entry point to describe an aggregate: a parent entity and its children, linked by a foreign key.</summary>
/// <example>
/// <code>
/// var persons = EntityGraph.For&lt;Person, long&gt;(p => p.Id)
///     .HasMany(p => p.Contacts, foreignKey: c => c.IdContact, key: c => c.Id, onLoaded: graph.AttachContacts);
///
/// await using var store = DommelEntityStore.Create(factory);
/// var result = await store.RunInTransactionAsync(s => persons.SaveAsync(s, person));
/// </code>
/// </example>
public static class EntityGraph
{
    /// <param name="key">Primary key of the parent. A parent whose key has its default value (0, Guid.Empty, null) is new.</param>
    public static EntityGraph<TParent, TKey> For<[DynamicallyAccessedMembers(EntityMembers.Required)] TParent, TKey>(
        Expression<Func<TParent, TKey>> key)
        where TParent : class
        where TKey : notnull
        => new(key);
}

/// <summary>
/// A parent entity and its relations, saved, removed and loaded together through any <see cref="IEntityStore"/>
/// (Dommel, EF Core...). Immutable once configured: build it once and share it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="HasMany"/> (one to many), <see cref="HasOne"/> (one to one), <see cref="HasManyThrough"/> (many to many,
/// through a table of links) and <see cref="References"/> (a navigation held by a foreign key of the parent). A child type
/// given its own graph is saved, removed and loaded with its own children: a graph has any number of levels.</item>
/// <item><see cref="SaveAsync"/> inserts or updates the parent, writes its key into the children's foreign key, then aligns
/// the stored children: the new ones are inserted, the missing ones deleted (their own children first), and only the
/// changed ones updated. A child that still has the values it was read with (<see cref="EntityOriginals"/>) is left alone,
/// even if its row changed meanwhile. A null collection means "not loaded": the stored children stay.</item>
/// <item><see cref="RemoveAsync"/> deletes the children (at every level), then the parent.</item>
/// <item><see cref="LoadAsync(IEntityStore, IReadOnlyList{TParent}, CancellationToken)"/> reads the relations of a whole list
/// with one query per relation and per level.</item>
/// <item><see cref="ExpandAsync(IEntityStore, Expression{Func{TParent, bool}}, CancellationToken)"/> turns the filters that go through a relation into conditions the store can run.</item>
/// </list>
/// Save and remove must run in a transaction, for example inside
/// <see cref="EntityStoreExtensions.RunInTransactionAsync(IEntityStore, Func{IEntityStore, Task}, CancellationToken)"/>,
/// or through <see cref="GraphRepository{TParent, TKey}"/>.
/// </remarks>
public sealed class EntityGraph<[DynamicallyAccessedMembers(EntityMembers.Required)] TParent, TKey> : IEntityGraph<TParent>
    where TParent : class
    where TKey : notnull
{
    private readonly Expression<Func<TParent, TKey>> _keyExpression;
    private readonly Func<TParent, TKey> _key;
    private readonly List<IRelation> _relations = [];

    private static readonly MethodInfo ContainsKey = new Func<IEnumerable<TKey>, TKey, bool>(Enumerable.Contains).Method;

    internal EntityGraph(Expression<Func<TParent, TKey>> key)
    {
        _keyExpression = key ?? throw new ArgumentNullException(nameof(key));
        _key = key.Compile();
    }

    /// <summary>Key of the parent.</summary>
    public TKey KeyOf(TParent parent) => _key(parent);

    /// <summary>True when the parent's key still has its default value: it is not in the database yet.</summary>
    public bool IsNew(TParent parent) => IsDefault(_key(parent));

    /// <inheritdoc/>
    public IReadOnlyList<GraphForeignKey> ForeignKeys => [.. _relations.SelectMany(r => r.ForeignKeys)];

    /// <summary>The names of the relations, in the order they were declared.</summary>
    public IReadOnlyList<string> Relations => [.. _relations.Select(r => r.Name)];

    /// <summary>Declares a child collection (one to many).</summary>
    /// <param name="children">Collection property of the parent, for example <c>p => p.Contacts</c>. It must be writable.</param>
    /// <param name="foreignKey">Child property holding the parent's key, for example <c>c => c.IdPerson</c>. It must be writable.</param>
    /// <param name="key">
    /// Primary key of the child: a default value means a new child; equal keys mean the same row. For a composite key, a
    /// tuple (<c>c => new ValueTuple&lt;long, long&gt;(c.OrderId, c.ProductId)</c>): a key not stored yet is then a new row.
    /// </param>
    /// <param name="onLoaded">
    /// Called after loading with the parent and its loaded children, for example to set navigation properties (the
    /// generated <c>{Parent}Graph</c> class does it).
    /// </param>
    /// <param name="graph">The graph of the child type: its own children are saved, removed and loaded with it.</param>
    public EntityGraph<TParent, TKey> HasMany<[DynamicallyAccessedMembers(EntityMembers.Required)] TChild, TChildKey>(
        Expression<Func<TParent, IEnumerable<TChild>?>> children,
        Expression<Func<TChild, TKey>> foreignKey,
        Expression<Func<TChild, TChildKey>> key,
        Action<TParent, IReadOnlyList<TChild>>? onLoaded = null,
        EntityGraph<TChild, TChildKey>? graph = null)
        where TChild : class
        where TChildKey : notnull
    {
        ArgumentNullException.ThrowIfNull(children);
        var get = children.Compile();
        var set = Setter<TParent, List<TChild>>(children, nameof(children), out var property);
        Add(new ChildRelation<TChild, TChildKey>(this, property.Name, single: false,
            parent => get(parent)?.ToList(), set, foreignKey, key, onLoaded, graph));
        return this;
    }

    /// <summary>Declares one child (one to one): a row of another table holding the parent's key (a person and its address).</summary>
    /// <param name="child">Property of the parent holding the child, for example <c>p => p.Address</c>. It must be writable.</param>
    /// <param name="nullDeletes">
    /// A null property deletes the stored child when true. False by default: null then means "not loaded", and the stored
    /// child stays.
    /// </param>
    /// <inheritdoc cref="HasMany" path="/param[@name='foreignKey' or @name='key' or @name='graph']"/>
    public EntityGraph<TParent, TKey> HasOne<[DynamicallyAccessedMembers(EntityMembers.Required)] TChild, TChildKey>(
        Expression<Func<TParent, TChild?>> child,
        Expression<Func<TChild, TKey>> foreignKey,
        Expression<Func<TChild, TChildKey>> key,
        Action<TParent, TChild?>? onLoaded = null,
        EntityGraph<TChild, TChildKey>? graph = null,
        bool nullDeletes = false)
        where TChild : class
        where TChildKey : notnull
    {
        ArgumentNullException.ThrowIfNull(child);
        var get = child.Compile();
        var set = Setter<TParent, TChild>(child, nameof(child), out var property);
        Add(new ChildRelation<TChild, TChildKey>(this, property.Name, single: true,
            parent => get(parent) is { } one ? [one] : nullDeletes ? [] : null,
            (parent, list) => set(parent, list.Count > 0 ? list[0] : null!),
            foreignKey, key, onLoaded is null ? null : (parent, list) => onLoaded(parent, list.Count > 0 ? list[0] : null), graph));
        return this;
    }

    /// <summary>
    /// Declares other entities linked through a table of links (many to many): a product and its tags through
    /// <c>ProductTag</c>. The linked entities are never written: saving aligns the links with the collection.
    /// </summary>
    /// <param name="targets">Collection property of the parent holding the linked entities. It must be writable.</param>
    /// <param name="linkForeignKey">Property of the link holding the parent's key, for example <c>l => l.ProductId</c>.</param>
    /// <param name="linkTarget">Property of the link holding the key of the linked entity, for example <c>l => l.TagId</c>.</param>
    /// <param name="targetKey">Key of the linked entity, for example <c>t => t.Id</c>.</param>
    /// <param name="onLoaded">Called after loading with the parent and its linked entities.</param>
    public EntityGraph<TParent, TKey> HasManyThrough<
        [DynamicallyAccessedMembers(EntityMembers.Required | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TLink,
        [DynamicallyAccessedMembers(EntityMembers.Required)] TTarget, TTargetKey>(
        Expression<Func<TParent, IEnumerable<TTarget>?>> targets,
        Expression<Func<TLink, TKey>> linkForeignKey,
        Expression<Func<TLink, TTargetKey>> linkTarget,
        Expression<Func<TTarget, TTargetKey>> targetKey,
        Action<TParent, IReadOnlyList<TTarget>>? onLoaded = null)
        where TLink : class, new()
        where TTarget : class
        where TTargetKey : notnull
    {
        ArgumentNullException.ThrowIfNull(targets);
        var get = targets.Compile();
        var set = Setter<TParent, List<TTarget>>(targets, nameof(targets), out var property);
        Add(new ThroughRelation<TLink, TTarget, TTargetKey>(this, property.Name, parent => get(parent)?.ToList(), set,
            linkForeignKey, linkTarget, targetKey, onLoaded));
        return this;
    }

    /// <summary>
    /// Declares a navigation held by a foreign key of the parent (an order and its customer): loading sets it, and filters may
    /// go through it. It is never written by a save.
    /// </summary>
    /// <param name="navigation">Property of the parent holding the other entity, for example <c>o => o.Customer</c>.</param>
    /// <param name="foreignKey">Name of the property of the parent holding the key of the other, for example <c>nameof(Order.CustomerId)</c>.</param>
    /// <param name="key">Key of the other entity, for example <c>c => c.Id</c>.</param>
    public EntityGraph<TParent, TKey> References<[DynamicallyAccessedMembers(EntityMembers.Required)] TTarget, TTargetKey>(
        Expression<Func<TParent, TTarget?>> navigation,
        string foreignKey,
        Expression<Func<TTarget, TTargetKey>> key)
        where TTarget : class
        where TTargetKey : notnull
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentException.ThrowIfNullOrEmpty(foreignKey);
        var set = Setter<TParent, TTarget>(navigation, nameof(navigation), out var property);
        var fk = typeof(TParent).GetProperty(foreignKey, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException($"{typeof(TParent).Name} has no property {foreignKey}.", nameof(foreignKey));
        if (fk.PropertyType != typeof(TTargetKey) && Nullable.GetUnderlyingType(fk.PropertyType) != typeof(TTargetKey))
            throw new ArgumentException($"{typeof(TParent).Name}.{foreignKey} is {fk.PropertyType.Name}, the key of {typeof(TTarget).Name} is {typeof(TTargetKey).Name}.", nameof(foreignKey));
        Add(new ReferenceRelation<TTarget, TTargetKey>(property.Name, set, fk, key));
        return this;
    }

    private void Add(IRelation relation)
    {
        if (_relations.Any(r => r.Name == relation.Name))
            throw new ArgumentException($"{typeof(TParent).Name}.{relation.Name} is declared twice.");
        _relations.Add(relation);
    }

    // ---------- Save, remove, load ----------

    /// <summary>
    /// Inserts or updates the parent and aligns its children, at every level. Throws <see cref="EntityStoreException"/>
    /// (NotFound) when an updated parent no longer exists. Run it in a transaction.
    /// </summary>
    public async Task SaveAsync(IEntityStore store, TParent parent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(parent);

        await store.SaveAsync(parent, IsNew, ct).ConfigureAwait(false);   // a generated key is written back into the parent
        await SaveRelationsAsync(store, parent, ct).ConfigureAwait(false);
    }

    /// <summary>Aligns the stored relations of a parent already written.</summary>
    internal async Task SaveRelationsAsync(IEntityStore store, TParent parent, CancellationToken ct)
    {
        var key = _key(parent);
        foreach (var relation in _relations)
            await relation.SaveAsync(store, parent, key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the children stored for the parent (theirs first), then the parent. Throws <see cref="EntityStoreException"/>
    /// (NotFound) when the parent no longer exists. Run it in a transaction.
    /// </summary>
    public async Task RemoveAsync(IEntityStore store, TParent parent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(parent);

        if (!IsNew(parent))
            await RemoveRelationsAsync(store, [_key(parent)], ct).ConfigureAwait(false);

        if (!await store.DeleteAsync(parent, ct).ConfigureAwait(false))
            throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TParent).Name} not found: nothing was deleted.");
    }

    /// <summary>Deletes what the relations hold for the parents of <paramref name="keys"/>, at every level.</summary>
    internal async Task RemoveRelationsAsync(IEntityStore store, IReadOnlyList<TKey> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
            return;
        foreach (var relation in _relations)
            await relation.RemoveAsync(store, keys, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the relations of the parent. A new parent gets empty collections.</summary>
    public Task LoadAsync(IEntityStore store, TParent parent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return LoadAsync(store, [parent], ct);
    }

    /// <summary>
    /// Reads the relations of every parent of <paramref name="parents"/>: one query per relation and per level (by groups of
    /// keys), whatever the number of parents. The children read are remembered (<see cref="EntityOriginals"/>).
    /// </summary>
    public async Task LoadAsync(IEntityStore store, IReadOnlyList<TParent> parents, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(parents);
        foreach (var relation in _relations)
            await relation.LoadAsync(store, parents, ct).ConfigureAwait(false);
    }

    /// <summary>Reads one relation of the parent, for example <c>nameof(Person.Contacts)</c>.</summary>
    /// <exception cref="ArgumentException">No relation of that name was declared.</exception>
    public Task LoadAsync(IEntityStore store, TParent parent, string collection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(collection);

        var relation = _relations.Find(r => r.Name == collection)
            ?? throw new ArgumentException($"{typeof(TParent).Name} has no relation named '{collection}'.", nameof(collection));
        return relation.LoadAsync(store, [parent], ct);
    }

    /// <inheritdoc/>
    public void Remember(TParent parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        foreach (var relation in _relations)
            relation.Remember(parent);
    }

    /// <inheritdoc/>
    public void CollectUnchanged(TParent parent, ISet<string> ids)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var relation in _relations)
            relation.CollectUnchanged(parent, ids);
    }

    // ---------- Relation filters ----------

    /// <summary>
    /// <paramref name="predicate"/> with the conditions that go through a relation turned into conditions on the parent's
    /// columns, from queries on <paramref name="store"/>:
    /// <list type="bullet">
    /// <item><c>p.Lines.Any()</c>, <c>p.Lines.Any(l =&gt; ...)</c>, <c>p.Lines.All(l =&gt; ...)</c>, <c>p.Lines.Count(...) &gt; 2</c>
    /// on a collection (one to many, many to many);</item>
    /// <item><c>p.Customer.City == "Paris"</c>, <c>p.Customer == null</c> on a navigation (<see cref="References"/>, <see cref="HasOne"/>).</item>
    /// </list>
    /// The related rows are read with the rules of the server when there are some (<see cref="RelationReadGuard"/>).
    /// </summary>
    /// <exception cref="NotSupportedException">A condition mixes a navigation with another property, or names a property no relation declares.</exception>
    public async Task<Expression<Func<TParent, bool>>> ExpandAsync(IEntityStore store, Expression<Func<TParent, bool>> predicate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(predicate);
        if (!EntityRelations.UsesRelations(predicate))
            return predicate;
        var parameter = predicate.Parameters[0];
        var body = await ExpandAsync(store, predicate.Body, parameter, ct).ConfigureAwait(false);
        return Expression.Lambda<Func<TParent, bool>>(body, parameter);
    }

    private async Task<Expression> ExpandAsync(IEntityStore store, Expression expression, ParameterExpression parameter, CancellationToken ct)
    {
        switch (expression)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } logical:
                return Expression.MakeBinary(logical.NodeType,
                    await ExpandAsync(store, logical.Left, parameter, ct).ConfigureAwait(false),
                    await ExpandAsync(store, logical.Right, parameter, ct).ConfigureAwait(false));

            case UnaryExpression { NodeType: ExpressionType.Not } not:
                return Expression.Not(await ExpandAsync(store, not.Operand, parameter, ct).ConfigureAwait(false));

            case BinaryExpression comparison when CountOf(comparison, parameter) is { } count:
                return await count.Relation.CountAsync(store, count.Inner, count.Operator, count.Value, parameter, ct).ConfigureAwait(false);

            case MethodCallExpression call when CollectionCall(call, parameter) is { } collection:
                if (collection.Method is "Count" or "LongCount")
                    throw Unsupported(call, "a count of a relation is compared with a number: p.Lines.Count() > 2");
                if (collection.Method == "All")
                {
                    // All(x) is "none that is not x".
                    var negated = Negate(collection.Inner!);
                    return Expression.Not(await collection.Relation.AnyAsync(store, negated, parameter, ct).ConfigureAwait(false));
                }
                return await collection.Relation.AnyAsync(store, collection.Inner, parameter, ct).ConfigureAwait(false);

            case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } nullCheck
                when NavigationNullCheck(nullCheck, parameter) is { } navigation:
                var isNull = await navigation.Relation.IsNullAsync(store, parameter, ct).ConfigureAwait(false);
                return nullCheck.NodeType == ExpressionType.Equal ? isNull : Expression.Not(isNull);
        }

        // A condition: on the parent's columns, or through one navigation.
        var used = new NavigationFinder(parameter, this);
        used.Visit(expression);
        if (used.Collections.Count > 0)
            throw Unsupported(expression, $"{string.Join(", ", used.Collections)} is a collection: filter it with Any, All or Count");
        if (used.Unknown.Count > 0)
            throw new NotSupportedException(
                $"{typeof(TParent).Name}.{string.Join(", ", used.Unknown)} is not a relation of its graph: declare it with [EntityGraph] or [EntityReference].");
        if (used.Navigations.Count == 0)
            return expression;
        if (used.Navigations.Count > 1 || used.Direct)
            throw Unsupported(expression, "a condition reads one navigation at a time, and nothing else of the entity");
        var relation = used.Navigations.Single();
        return await relation.MatchAsync(store, target => new Replacer(e => e is MemberExpression m && m.Expression == parameter
            && m.Member.Name == relation.Name ? target : null).Visit(expression)!, parameter, ct).ConfigureAwait(false);
    }

    private static LambdaExpression Negate(LambdaExpression predicate)
        => Expression.Lambda(predicate.Type, Expression.Not(predicate.Body), predicate.Parameters);

    private sealed record CollectionUse(IRelation Relation, string Method, LambdaExpression? Inner);

    private CollectionUse? CollectionCall(MethodCallExpression call, ParameterExpression parameter)
    {
        if (call.Method.DeclaringType != typeof(Enumerable) || call.Arguments.Count is 0 or > 2
            || call.Method.Name is not ("Any" or "All" or "Count" or "LongCount")
            || Strip(call.Arguments[0]) is not MemberExpression { Expression: var owner } member || owner != parameter
            || _relations.Find(r => r.Name == member.Member.Name) is not { IsCollection: true } relation)
            return null;
        var inner = call.Arguments.Count == 2 ? Strip(call.Arguments[1]) as LambdaExpression : null;
        if (call.Arguments.Count == 2 && inner is null)
            throw Unsupported(call, "the condition of a relation is a lambda written in place");
        if (inner is not null && UsesParameter(inner.Body, parameter))
            throw Unsupported(call, $"the condition on {relation.Name} uses the {typeof(TParent).Name} itself");
        return new CollectionUse(relation, call.Method.Name, inner);
    }

    private sealed record CountUse(IRelation Relation, LambdaExpression? Inner, ExpressionType Operator, long Value);

    private CountUse? CountOf(BinaryExpression comparison, ParameterExpression parameter)
    {
        if (comparison.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan
            or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual))
            return null;
        foreach (var (side, other, mirrored) in new[] { (comparison.Left, comparison.Right, false), (comparison.Right, comparison.Left, true) })
        {
            if (Strip(side) is MethodCallExpression call && CollectionCall(call, parameter) is { Method: "Count" or "LongCount" } use
                && !UsesParameter(other, parameter) && CapturedValues.TryEvaluate(Strip(other), out var value) && value is not null)
            {
                var op = mirrored ? Mirror(comparison.NodeType) : comparison.NodeType;
                return new CountUse(use.Relation, use.Inner, op, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return null;
    }

    private sealed record NullCheck(IRelation Relation);

    private NullCheck? NavigationNullCheck(BinaryExpression comparison, ParameterExpression parameter)
    {
        var (left, right) = (Strip(comparison.Left), Strip(comparison.Right));
        var member = left is ConstantExpression { Value: null } ? right : right is ConstantExpression { Value: null } ? left : null;
        return member is MemberExpression { Expression: var owner } navigation && owner == parameter
               && _relations.Find(r => r.Name == navigation.Member.Name) is { IsCollection: false } relation
            ? new NullCheck(relation)
            : null;
    }

    private static ExpressionType Mirror(ExpressionType type) => type switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => type,
    };

    private NotSupportedException Unsupported(Expression expression, string rule)
        => new($"The filter '{expression}' on {typeof(TParent).Name} cannot be run: {rule}.");

    /// <summary>The navigations a condition reads (p.Customer.Name), and whether it reads the parent's own columns too.</summary>
    private sealed class NavigationFinder(ParameterExpression parameter, EntityGraph<TParent, TKey> graph) : ExpressionVisitor
    {
        public HashSet<IRelation> Navigations { get; } = [];
        public HashSet<string> Collections { get; } = [];
        public HashSet<string> Unknown { get; } = [];
        public bool Direct { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression != parameter)
                return base.VisitMember(node);
            if (EntityValues.IsSimple(node.Type))
                Direct = true;
            else if (graph._relations.Find(r => r.Name == node.Member.Name) is not { } relation)
                Unknown.Add(node.Member.Name);
            else if (relation.IsCollection)
                Collections.Add(node.Member.Name);
            else
                Navigations.Add(relation);
            return node;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter)
                Direct = true;          // the entity itself, compared or passed as a whole
            return node;
        }
    }

    // ---------- Helpers ----------

    /// <summary><c>keys.Contains(key of the parent)</c>, the parent's key read from <paramref name="parameter"/>.</summary>
    private Expression KeyIn(IReadOnlyCollection<TKey> keys, ParameterExpression parameter)
    {
        if (keys.Count > EntityRelations.MaxKeys)
            throw TooManyKeys(keys.Count);
        var key = new Replacer(e => e == _keyExpression.Parameters[0] ? parameter : null).Visit(_keyExpression.Body)!;
        return keys.Count == 0
            ? Expression.Constant(false)
            : Expression.Call(ContainsKey, Expression.Constant(keys.ToArray()), key);
    }

    private static NotSupportedException TooManyKeys(int count)
        => new($"The relation filter matches {count} rows, more than {EntityRelations.MaxKeys} (EntityRelations.MaxKeys): narrow it.");

    private static IEnumerable<TValue[]> Chunks<TValue>(IEnumerable<TValue> values)
        => values.Chunk(Math.Max(1, Math.Min(EntityRelations.MaxKeys, 1000)));

    private static bool IsDefault<T>(T value) => value is null || EqualityComparer<T>.Default.Equals(value, default!);

    private static string MemberName(LambdaExpression selector)
        => Strip(selector.Body) is MemberExpression member ? member.Member.Name : string.Empty;

    private static Expression Strip(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Quote } unary)
            expression = unary.Operand;
        return expression;
    }

    private static bool UsesParameter(Expression expression, ParameterExpression parameter)
    {
        var found = false;
        new Replacer(e =>
        {
            found |= e == parameter;
            return null;
        }).Visit(expression);
        return found;
    }

    /// <summary>
    /// <c>c =&gt; keys.Contains(c.ForeignKey)</c> for a key of type <typeparamref name="TValue"/>; a nullable foreign key is
    /// tested for null first, so that the same predicate runs in memory.
    /// </summary>
    private static Expression<Func<T, bool>> In<T, TValue>(Expression<Func<T, TValue>> value, IEnumerable<TValue> keys)
    {
        var contains = new Func<IEnumerable<TValue>, TValue, bool>(Enumerable.Contains).Method;
        var parameter = value.Parameters[0];
        var body = value.Body;
        Expression test;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert } convert && Nullable.GetUnderlyingType(convert.Operand.Type) is not null)
            test = Expression.AndAlso(Expression.NotEqual(convert.Operand, Expression.Constant(null, convert.Operand.Type)),
                Expression.Call(contains, Expression.Constant(keys.ToArray()), body));
        else
            test = Expression.Call(contains, Expression.Constant(keys.ToArray()), body);
        return Expression.Lambda<Func<T, bool>>(test, parameter);
    }

    private static bool Compare(long count, ExpressionType op, long value) => op switch
    {
        ExpressionType.Equal => count == value,
        ExpressionType.NotEqual => count != value,
        ExpressionType.GreaterThan => count > value,
        ExpressionType.GreaterThanOrEqual => count >= value,
        ExpressionType.LessThan => count < value,
        _ => count <= value,
    };

    /// <summary>Compiles an assignment to the property selected by <paramref name="selector"/>.</summary>
    private static Action<TOwner, TValue> Setter<TOwner, TValue>(LambdaExpression selector, string argument, out PropertyInfo selected)
    {
        var body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            body = convert.Operand;

        if (body is not MemberExpression { Member: PropertyInfo { CanWrite: true } property } member
            || member.Expression != selector.Parameters[0])
            throw new ArgumentException($"{selector} must select a writable property, for example x => x.Property.", argument);

        if (!property.PropertyType.IsAssignableFrom(typeof(TValue))
            && Nullable.GetUnderlyingType(property.PropertyType) != typeof(TValue))
            throw new ArgumentException(
                $"Property {property.Name} ({property.PropertyType.Name}) must accept a value of type {typeof(TValue).Name}.", argument);

        selected = property;
        var owner = Expression.Parameter(typeof(TOwner), "owner");
        var value = Expression.Parameter(typeof(TValue), "value");
        var assign = Expression.Assign(Expression.Property(owner, property), Expression.Convert(value, property.PropertyType));
        return Expression.Lambda<Action<TOwner, TValue>>(assign, owner, value).Compile();
    }

    /// <summary>Replaces the nodes <paramref name="replace"/> gives a replacement for.</summary>
    private sealed class Replacer(Func<Expression, Expression?> replace) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node)
            => node is not null && replace(node) is { } replacement ? replacement : base.Visit(node);
    }

    // ---------- Relations ----------

    private interface IRelation
    {
        string Name { get; }

        /// <summary>A collection (Any, All, Count), rather than one entity (a condition on its columns, a null check).</summary>
        bool IsCollection { get; }

        IEnumerable<GraphForeignKey> ForeignKeys { get; }

        Task SaveAsync(IEntityStore store, TParent parent, TKey key, CancellationToken ct);

        Task RemoveAsync(IEntityStore store, IReadOnlyList<TKey> keys, CancellationToken ct);

        Task LoadAsync(IEntityStore store, IReadOnlyList<TParent> parents, CancellationToken ct);

        void Remember(TParent parent);

        void CollectUnchanged(TParent parent, ISet<string> ids);

        /// <summary>The parents having at least one related row matching <paramref name="inner"/> (every row when null).</summary>
        Task<Expression> AnyAsync(IEntityStore store, LambdaExpression? inner, ParameterExpression parent, CancellationToken ct);

        /// <summary>The parents whose number of related rows matching <paramref name="inner"/> compares as asked with <paramref name="value"/>.</summary>
        Task<Expression> CountAsync(IEntityStore store, LambdaExpression? inner, ExpressionType op, long value, ParameterExpression parent, CancellationToken ct);

        /// <summary>The parents whose related entity matches the condition <paramref name="body"/> builds on it.</summary>
        Task<Expression> MatchAsync(IEntityStore store, Func<ParameterExpression, Expression> body, ParameterExpression parent, CancellationToken ct);

        /// <summary>The parents without a related entity.</summary>
        Task<Expression> IsNullAsync(IEntityStore store, ParameterExpression parent, CancellationToken ct);
    }

    /// <summary>Children holding the parent's key: a collection (one to many) or one child (one to one).</summary>
    private sealed class ChildRelation<[DynamicallyAccessedMembers(EntityMembers.Required)] TChild, TChildKey> : IRelation
        where TChild : class
        where TChildKey : notnull
    {
        private readonly EntityGraph<TParent, TKey> _owner;
        private readonly bool _single;
        private readonly Func<TParent, List<TChild>?> _current;
        private readonly Action<TParent, List<TChild>> _assign;
        private readonly Expression<Func<TChild, TKey>> _foreignKey;
        private readonly Func<TChild, TKey> _getForeignKey;
        private readonly Action<TChild, TKey> _setForeignKey;
        private readonly Expression<Func<TChild, TChildKey>> _keyExpression;
        private readonly Func<TChild, TChildKey> _childKey;
        private readonly Action<TParent, IReadOnlyList<TChild>>? _onLoaded;
        private readonly EntityGraph<TChild, TChildKey>? _graph;
        private readonly bool _keyAssigned;

        public ChildRelation(EntityGraph<TParent, TKey> owner, string name, bool single, Func<TParent, List<TChild>?> current,
            Action<TParent, List<TChild>> assign, Expression<Func<TChild, TKey>> foreignKey, Expression<Func<TChild, TChildKey>> key,
            Action<TParent, IReadOnlyList<TChild>>? onLoaded, EntityGraph<TChild, TChildKey>? graph)
        {
            ArgumentNullException.ThrowIfNull(foreignKey);
            ArgumentNullException.ThrowIfNull(key);
            _owner = owner;
            Name = name;
            _single = single;
            _current = current;
            _assign = assign;
            _foreignKey = foreignKey;
            _getForeignKey = foreignKey.Compile();
            _setForeignKey = Setter<TChild, TKey>(foreignKey, nameof(foreignKey), out _);
            _keyExpression = key;
            _childKey = key.Compile();
            _onLoaded = onLoaded;
            _graph = graph;
            if (graph is not null && !ReferenceEquals(graph, owner))
                EntityRelations.TryRegister<TChild>(() => graph);

            // A key the application gives (a composite key, or [DatabaseGenerated(None)]): a key not stored yet is a new row.
            var keys = SyncMetadata.Keys(typeof(TChild));
            _keyAssigned = typeof(ITuple).IsAssignableFrom(typeof(TChildKey)) || keys.Count > 1
                || keys.Count == 1 && keys[0].GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedAttribute>()
                    is { DatabaseGeneratedOption: System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.None };
        }

        public string Name { get; }

        public bool IsCollection => !_single;

        public IEnumerable<GraphForeignKey> ForeignKeys => [new(typeof(TChild), MemberName(_foreignKey), typeof(TParent))];

        private EntityGraph<TChild, TChildKey>? Graph => ReferenceEquals(_graph, _owner) ? (EntityGraph<TChild, TChildKey>)(object)_owner : _graph;

        public async Task SaveAsync(IEntityStore store, TParent parent, TKey key, CancellationToken ct)
        {
            // Null: the children were not loaded, they stay as they are. Deleting them would lose data.
            if (_current(parent) is not { } current)
                return;

            var before = await store.SelectAsync(ForeignKeyEquals(key), ct).ConfigureAwait(false);
            var stored = before.ToDictionary(_childKey);

            foreach (var child in current)
                _setForeignKey(child, key);

            // Each child: new, unchanged, or changed. An existing child must already belong to this parent: otherwise the
            // update would move a row of another parent here (a remote client could take over any child by its key).
            var inserts = new List<TChild>();
            var updates = new List<(TChild Child, TChild Stored)>();
            var kept = new HashSet<TChildKey>();
            foreach (var child in current)
            {
                var childKey = _childKey(child);
                if (stored.TryGetValue(childKey, out var row))
                {
                    if (!kept.Add(childKey))
                        throw new EntityStoreException(EntityErrorCode.Conflict, $"{typeof(TChild).Name} {childKey} is twice in {Name}.");
                    if (!GraphChanges.Contains(typeof(TChild), childKey) && !EntityOriginals.IsUnchanged(child) && !EntityValues.Same(child, row))
                        updates.Add((child, row));
                }
                else if (IsDefault(childKey) || _keyAssigned)
                {
                    inserts.Add(child);
                }
                else
                {
                    throw new EntityStoreException(EntityErrorCode.Conflict,
                        $"{typeof(TChild).Name} {childKey} does not belong to this {typeof(TParent).Name}.");
                }
            }
            if (_single && current.Count > 1)
                throw new EntityStoreException(EntityErrorCode.InvalidRequest, $"{typeof(TParent).Name}.{Name} holds one child.");
            var removed = before.Where(b => !kept.Contains(_childKey(b))).ToList();

            // Sent by a remote client: each child written follows the rules of its own type before anything is written.
            if (GraphWriteGuard.Current is { } guard)
            {
                foreach (var child in inserts)
                    await guard.CheckAsync(store, GraphWrite.Insert, child, null, ct).ConfigureAwait(false);
                foreach (var (child, row) in updates)
                    await guard.CheckAsync(store, GraphWrite.Update, child, row, ct).ConfigureAwait(false);
                foreach (var child in removed)
                    await guard.CheckAsync(store, GraphWrite.Delete, child, child, ct).ConfigureAwait(false);
            }

            // Deletes first (their own children before them): a child replaced by a new one does not violate a unique constraint.
            if (removed.Count > 0)
            {
                if (Graph is { } graph)
                    await graph.RemoveRelationsAsync(store, [.. removed.Select(_childKey)], ct).ConfigureAwait(false);
                foreach (var child in removed)
                    await store.DeleteAsync(child, ct).ConfigureAwait(false);
            }
            foreach (var child in inserts)
                await store.InsertAsync(child, ct).ConfigureAwait(false);
            foreach (var (child, _) in updates)
            {
                if (!await store.UpdateAsync(child, ct).ConfigureAwait(false))
                    throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TChild).Name} not found: nothing was updated.");
            }

            if (Graph is { } children)
            {
                foreach (var child in current)
                    await children.SaveRelationsAsync(store, child, ct).ConfigureAwait(false);
            }
        }

        public async Task RemoveAsync(IEntityStore store, IReadOnlyList<TKey> keys, CancellationToken ct)
        {
            var children = new List<TChild>();
            foreach (var chunk in Chunks(keys.Distinct()))
                children.AddRange(await store.SelectAsync(In(_foreignKey, chunk), ct).ConfigureAwait(false));
            if (GraphWriteGuard.Current is { } guard)
            {
                foreach (var child in children)
                    await guard.CheckAsync(store, GraphWrite.Delete, child, child, ct).ConfigureAwait(false);
            }
            if (Graph is { } graph && children.Count > 0)
                await graph.RemoveRelationsAsync(store, [.. children.Select(_childKey)], ct).ConfigureAwait(false);
            foreach (var child in children)
                await store.DeleteAsync(child, ct).ConfigureAwait(false);
        }

        public async Task LoadAsync(IEntityStore store, IReadOnlyList<TParent> parents, CancellationToken ct)
        {
            var keys = parents.Where(p => !_owner.IsNew(p)).Select(_owner._key).Distinct().ToList();
            var loaded = new List<TChild>();
            foreach (var chunk in Chunks(keys))
                loaded.AddRange(await store.SelectAsync(In(_foreignKey, chunk), ct).ConfigureAwait(false));

            var byParent = loaded.GroupBy(_getForeignKey).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var parent in parents)
            {
                var children = !_owner.IsNew(parent) && byParent.TryGetValue(_owner._key(parent), out var found) ? found : [];
                _assign(parent, children);
                _onLoaded?.Invoke(parent, children);
            }
            foreach (var child in loaded)
                EntityOriginals.Remember(child);
            if (Graph is { } graph && loaded.Count > 0)
                await graph.LoadAsync(store, loaded, ct).ConfigureAwait(false);
        }

        public void Remember(TParent parent)
        {
            foreach (var child in _current(parent) ?? [])
            {
                EntityOriginals.Remember(child);
                Graph?.Remember(child);
            }
        }

        public void CollectUnchanged(TParent parent, ISet<string> ids)
        {
            foreach (var child in _current(parent) ?? [])
            {
                if (!IsDefault(_childKey(child)) && EntityOriginals.IsUnchanged(child))
                    ids.Add(GraphChanges.IdOf(typeof(TChild), _childKey(child)));
                Graph?.CollectUnchanged(child, ids);
            }
        }

        public async Task<Expression> AnyAsync(IEntityStore store, LambdaExpression? inner, ParameterExpression parent, CancellationToken ct)
        {
            var keys = await ParentKeysAsync(store, inner as Expression<Func<TChild, bool>> ?? All, ct).ConfigureAwait(false);
            return _owner.KeyIn(keys, parent);
        }

        public async Task<Expression> CountAsync(IEntityStore store, LambdaExpression? inner, ExpressionType op, long value,
            ParameterExpression parent, CancellationToken ct)
        {
            var predicate = await RelationReadGuard.RestrictAsync(inner as Expression<Func<TChild, bool>> ?? All, ct).ConfigureAwait(false);
            var groups = await store.AggregateAsync(predicate, Aggregation.Of<TChild>().GroupBy(_foreignKey).Count(), ct).ConfigureAwait(false);
            var counted = groups.Where(g => g.Key is not null).Select(g => (Key: (TKey)g.Key!, g.Count)).ToList();

            // A parent without a related row counts 0: it is kept when 0 passes the test, so the test is written on those that fail.
            if (!Compare(0, op, value))
                return _owner.KeyIn([.. counted.Where(g => Compare(g.Count, op, value)).Select(g => g.Key)], parent);
            return Expression.Not(_owner.KeyIn([.. counted.Where(g => !Compare(g.Count, op, value)).Select(g => g.Key)], parent));
        }

        public async Task<Expression> MatchAsync(IEntityStore store, Func<ParameterExpression, Expression> body, ParameterExpression parent,
            CancellationToken ct)
        {
            var target = Expression.Parameter(typeof(TChild), "c");
            var keys = await ParentKeysAsync(store, Expression.Lambda<Func<TChild, bool>>(body(target), target), ct).ConfigureAwait(false);
            return _owner.KeyIn(keys, parent);
        }

        public async Task<Expression> IsNullAsync(IEntityStore store, ParameterExpression parent, CancellationToken ct)
            => Expression.Not(await AnyAsync(store, null, parent, ct).ConfigureAwait(false));

        private static readonly Expression<Func<TChild, bool>> All = _ => true;

        /// <summary>The keys of the parents having a child matching <paramref name="inner"/>, among the rows the user may read.</summary>
        private async Task<IReadOnlyCollection<TKey>> ParentKeysAsync(IEntityStore store, Expression<Func<TChild, bool>> inner, CancellationToken ct)
        {
            var predicate = await RelationReadGuard.RestrictAsync(inner, ct).ConfigureAwait(false);
            var keys = await store.SelectAsync(predicate, _foreignKey, ct).ConfigureAwait(false);
            return keys.Where(k => !IsDefault(k)).ToHashSet();
        }

        /// <summary>
        /// <c>c =&gt; c.ForeignKey == key</c>: translatable by Dommel, EF and the remote filters. A nullable foreign key is
        /// tested for null first, so that it runs in memory too (a tree: the root has no parent).
        /// </summary>
        private Expression<Func<TChild, bool>> ForeignKeyEquals(TKey key)
        {
            var body = _foreignKey.Body;
            var equal = Expression.Equal(body, Expression.Constant(key, typeof(TKey)));
            return Expression.Lambda<Func<TChild, bool>>(
                body is UnaryExpression { NodeType: ExpressionType.Convert } convert && Nullable.GetUnderlyingType(convert.Operand.Type) is not null
                    ? Expression.AndAlso(Expression.NotEqual(convert.Operand, Expression.Constant(null, convert.Operand.Type)), equal)
                    : equal,
                _foreignKey.Parameters);
        }
    }

    /// <summary>Entities linked to the parent through a table of links (many to many): the links are written, never the entities.</summary>
    private sealed class ThroughRelation<
        [DynamicallyAccessedMembers(EntityMembers.Required | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TLink,
        [DynamicallyAccessedMembers(EntityMembers.Required)] TTarget, TTargetKey> : IRelation
        where TLink : class, new()
        where TTarget : class
        where TTargetKey : notnull
    {
        private readonly EntityGraph<TParent, TKey> _owner;
        private readonly Func<TParent, List<TTarget>?> _current;
        private readonly Action<TParent, List<TTarget>> _assign;
        private readonly Expression<Func<TLink, TKey>> _linkForeignKey;
        private readonly Func<TLink, TKey> _getLinkForeignKey;
        private readonly Action<TLink, TKey> _setLinkForeignKey;
        private readonly Expression<Func<TLink, TTargetKey>> _linkTarget;
        private readonly Func<TLink, TTargetKey> _getLinkTarget;
        private readonly Action<TLink, TTargetKey> _setLinkTarget;
        private readonly Expression<Func<TTarget, TTargetKey>> _targetKey;
        private readonly Func<TTarget, TTargetKey> _getTargetKey;
        private readonly Action<TParent, IReadOnlyList<TTarget>>? _onLoaded;

        public ThroughRelation(EntityGraph<TParent, TKey> owner, string name, Func<TParent, List<TTarget>?> current,
            Action<TParent, List<TTarget>> assign, Expression<Func<TLink, TKey>> linkForeignKey, Expression<Func<TLink, TTargetKey>> linkTarget,
            Expression<Func<TTarget, TTargetKey>> targetKey, Action<TParent, IReadOnlyList<TTarget>>? onLoaded)
        {
            ArgumentNullException.ThrowIfNull(linkForeignKey);
            ArgumentNullException.ThrowIfNull(linkTarget);
            ArgumentNullException.ThrowIfNull(targetKey);
            _owner = owner;
            Name = name;
            _current = current;
            _assign = assign;
            _linkForeignKey = linkForeignKey;
            _getLinkForeignKey = linkForeignKey.Compile();
            _setLinkForeignKey = Setter<TLink, TKey>(linkForeignKey, nameof(linkForeignKey), out _);
            _linkTarget = linkTarget;
            _getLinkTarget = linkTarget.Compile();
            _setLinkTarget = Setter<TLink, TTargetKey>(linkTarget, nameof(linkTarget), out _);
            _targetKey = targetKey;
            _getTargetKey = targetKey.Compile();
            _onLoaded = onLoaded;
        }

        public string Name { get; }

        public bool IsCollection => true;

        public IEnumerable<GraphForeignKey> ForeignKeys =>
            [new(typeof(TLink), MemberName(_linkForeignKey), typeof(TParent)), new(typeof(TLink), MemberName(_linkTarget), typeof(TTarget))];

        public async Task SaveAsync(IEntityStore store, TParent parent, TKey key, CancellationToken ct)
        {
            if (_current(parent) is not { } current)
                return;
            var wanted = new HashSet<TTargetKey>();
            foreach (var target in current)
            {
                var targetKey = _getTargetKey(target);
                if (IsDefault(targetKey))
                    throw new EntityStoreException(EntityErrorCode.InvalidRequest,
                        $"{typeof(TParent).Name}.{Name}: a {typeof(TTarget).Name} is linked before it is saved. Save it first.");
                wanted.Add(targetKey);
            }

            var links = await store.SelectAsync(In(_linkForeignKey, [key]), ct).ConfigureAwait(false);
            var existing = links.Select(_getLinkTarget).ToHashSet();
            var removed = links.Where(l => !wanted.Contains(_getLinkTarget(l))).ToList();
            var added = wanted.Where(k => !existing.Contains(k)).Select(k =>
            {
                var link = new TLink();
                _setLinkForeignKey(link, key);
                _setLinkTarget(link, k);
                return link;
            }).ToList();

            if (GraphWriteGuard.Current is { } guard)
            {
                foreach (var link in added)
                    await guard.CheckAsync(store, GraphWrite.Insert, link, null, ct).ConfigureAwait(false);
                foreach (var link in removed)
                    await guard.CheckAsync(store, GraphWrite.Delete, link, link, ct).ConfigureAwait(false);
            }
            foreach (var link in removed)
                await store.DeleteAsync(link, ct).ConfigureAwait(false);
            foreach (var link in added)
                await store.InsertAsync(link, ct).ConfigureAwait(false);
        }

        public async Task RemoveAsync(IEntityStore store, IReadOnlyList<TKey> keys, CancellationToken ct)
        {
            var links = new List<TLink>();
            foreach (var chunk in Chunks(keys.Distinct()))
                links.AddRange(await store.SelectAsync(In(_linkForeignKey, chunk), ct).ConfigureAwait(false));
            if (GraphWriteGuard.Current is { } guard)
            {
                foreach (var link in links)
                    await guard.CheckAsync(store, GraphWrite.Delete, link, link, ct).ConfigureAwait(false);
            }
            foreach (var link in links)
                await store.DeleteAsync(link, ct).ConfigureAwait(false);
        }

        public async Task LoadAsync(IEntityStore store, IReadOnlyList<TParent> parents, CancellationToken ct)
        {
            var keys = parents.Where(p => !_owner.IsNew(p)).Select(_owner._key).Distinct().ToList();
            var links = new List<TLink>();
            foreach (var chunk in Chunks(keys))
                links.AddRange(await store.SelectAsync(In(_linkForeignKey, chunk), ct).ConfigureAwait(false));
            var targets = new Dictionary<TTargetKey, TTarget>();
            foreach (var chunk in Chunks(links.Select(_getLinkTarget).Distinct()))
            {
                foreach (var target in await store.SelectAsync(In(_targetKey, chunk), ct).ConfigureAwait(false))
                    targets[_getTargetKey(target)] = target;
            }

            var byParent = links.GroupBy(_getLinkForeignKey).ToDictionary(g => g.Key,
                g => g.Select(l => targets.GetValueOrDefault(_getLinkTarget(l))).Where(t => t is not null).Select(t => t!).ToList());
            foreach (var parent in parents)
            {
                var linked = !_owner.IsNew(parent) && byParent.TryGetValue(_owner._key(parent), out var found) ? found : [];
                _assign(parent, linked);
                _onLoaded?.Invoke(parent, linked);
            }
        }

        public void Remember(TParent parent)
        {
        }

        public void CollectUnchanged(TParent parent, ISet<string> ids)
        {
        }

        public async Task<Expression> AnyAsync(IEntityStore store, LambdaExpression? inner, ParameterExpression parent, CancellationToken ct)
            => _owner.KeyIn(await ParentKeysAsync(store, inner as Expression<Func<TTarget, bool>>, ct).ConfigureAwait(false), parent);

        public async Task<Expression> CountAsync(IEntityStore store, LambdaExpression? inner, ExpressionType op, long value,
            ParameterExpression parent, CancellationToken ct)
        {
            var links = await LinksAsync(store, inner as Expression<Func<TTarget, bool>>, ct).ConfigureAwait(false);
            var counted = links.GroupBy(_getLinkForeignKey).Select(g => (g.Key, Count: (long)g.Count())).ToList();
            if (!Compare(0, op, value))
                return _owner.KeyIn([.. counted.Where(g => Compare(g.Count, op, value)).Select(g => g.Key)], parent);
            return Expression.Not(_owner.KeyIn([.. counted.Where(g => !Compare(g.Count, op, value)).Select(g => g.Key)], parent));
        }

        public Task<Expression> MatchAsync(IEntityStore store, Func<ParameterExpression, Expression> body, ParameterExpression parent, CancellationToken ct)
            => throw new NotSupportedException($"{typeof(TParent).Name}.{Name} is a collection: filter it with Any, All or Count.");

        public Task<Expression> IsNullAsync(IEntityStore store, ParameterExpression parent, CancellationToken ct)
            => throw new NotSupportedException($"{typeof(TParent).Name}.{Name} is a collection: test it with Any.");

        private async Task<IReadOnlyCollection<TKey>> ParentKeysAsync(IEntityStore store, Expression<Func<TTarget, bool>>? inner, CancellationToken ct)
            => (await LinksAsync(store, inner, ct).ConfigureAwait(false)).Select(_getLinkForeignKey).ToHashSet();

        /// <summary>The links (that the user may read) to the entities matching <paramref name="inner"/>; every link when null.</summary>
        private async Task<IReadOnlyList<TLink>> LinksAsync(IEntityStore store, Expression<Func<TTarget, bool>>? inner, CancellationToken ct)
        {
            Expression<Func<TLink, bool>> links = _ => true;
            if (inner is not null)
            {
                var targets = await store.SelectAsync(await RelationReadGuard.RestrictAsync(inner, ct).ConfigureAwait(false), _targetKey, ct)
                    .ConfigureAwait(false);
                var keys = targets.ToHashSet();
                if (keys.Count > EntityRelations.MaxKeys)
                    throw TooManyKeys(keys.Count);
                if (keys.Count == 0)
                    return [];
                links = In(_linkTarget, keys);
            }
            return await store.SelectAsync(await RelationReadGuard.RestrictAsync(links, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
    }

    /// <summary>A navigation held by a foreign key of the parent: read, filtered on, never written.</summary>
    private sealed class ReferenceRelation<[DynamicallyAccessedMembers(EntityMembers.Required)] TTarget, TTargetKey> : IRelation
        where TTarget : class
        where TTargetKey : notnull
    {
        private readonly Action<TParent, TTarget> _set;
        private readonly PropertyInfo _foreignKey;
        private readonly Expression<Func<TTarget, TTargetKey>> _targetKey;
        private readonly Func<TTarget, TTargetKey> _getTargetKey;
        private static readonly MethodInfo Contains = new Func<IEnumerable<TTargetKey>, TTargetKey, bool>(Enumerable.Contains).Method;

        public ReferenceRelation(string name, Action<TParent, TTarget> set, PropertyInfo foreignKey, Expression<Func<TTarget, TTargetKey>> targetKey)
        {
            ArgumentNullException.ThrowIfNull(targetKey);
            Name = name;
            _set = set;
            _foreignKey = foreignKey;
            _targetKey = targetKey;
            _getTargetKey = targetKey.Compile();
        }

        public string Name { get; }

        public bool IsCollection => false;

        public IEnumerable<GraphForeignKey> ForeignKeys => [new(typeof(TParent), _foreignKey.Name, typeof(TTarget))];

        public Task SaveAsync(IEntityStore store, TParent parent, TKey key, CancellationToken ct) => Task.CompletedTask;

        public Task RemoveAsync(IEntityStore store, IReadOnlyList<TKey> keys, CancellationToken ct) => Task.CompletedTask;

        public async Task LoadAsync(IEntityStore store, IReadOnlyList<TParent> parents, CancellationToken ct)
        {
            var keys = parents.Select(p => _foreignKey.GetValue(p)).OfType<TTargetKey>().Where(k => !IsDefault(k)).Distinct().ToList();
            var targets = new Dictionary<TTargetKey, TTarget>();
            foreach (var chunk in Chunks(keys))
            {
                foreach (var target in await store.SelectAsync(In(_targetKey, chunk), ct).ConfigureAwait(false))
                    targets[_getTargetKey(target)] = target;
            }
            foreach (var parent in parents)
            {
                if (_foreignKey.GetValue(parent) is TTargetKey key && targets.TryGetValue(key, out var target))
                    _set(parent, target);
            }
        }

        public void Remember(TParent parent)
        {
        }

        public void CollectUnchanged(TParent parent, ISet<string> ids)
        {
        }

        public Task<Expression> AnyAsync(IEntityStore store, LambdaExpression? inner, ParameterExpression parent, CancellationToken ct)
            => throw new NotSupportedException($"{typeof(TParent).Name}.{Name} is one entity: write a condition on it, or test it with == null.");

        public Task<Expression> CountAsync(IEntityStore store, LambdaExpression? inner, ExpressionType op, long value, ParameterExpression parent,
            CancellationToken ct) => AnyAsync(store, inner, parent, ct);

        public async Task<Expression> MatchAsync(IEntityStore store, Func<ParameterExpression, Expression> body, ParameterExpression parent,
            CancellationToken ct)
        {
            var target = Expression.Parameter(typeof(TTarget), "t");
            var predicate = await RelationReadGuard.RestrictAsync(Expression.Lambda<Func<TTarget, bool>>(body(target), target), ct).ConfigureAwait(false);
            var keys = (await store.SelectAsync(predicate, _targetKey, ct).ConfigureAwait(false)).ToHashSet();
            if (keys.Count > EntityRelations.MaxKeys)
                throw TooManyKeys(keys.Count);
            if (keys.Count == 0)
                return Expression.Constant(false);

            // p.CustomerId != null && keys.Contains((long)p.CustomerId): the same test in memory and in a database.
            var foreignKey = Expression.Property(parent, _foreignKey);
            var contains = Expression.Call(Contains, Expression.Constant(keys.ToArray()),
                foreignKey.Type == typeof(TTargetKey) ? foreignKey : Expression.Convert(foreignKey, typeof(TTargetKey)));
            return foreignKey.Type.IsValueType && Nullable.GetUnderlyingType(foreignKey.Type) is null
                ? contains
                : Expression.AndAlso(Expression.NotEqual(foreignKey, Expression.Constant(null, foreignKey.Type)), contains);
        }

        public Task<Expression> IsNullAsync(IEntityStore store, ParameterExpression parent, CancellationToken ct)
        {
            var foreignKey = Expression.Property(parent, _foreignKey);
            return Task.FromResult<Expression>(foreignKey.Type.IsValueType && Nullable.GetUnderlyingType(foreignKey.Type) is null
                ? Expression.Constant(false)
                : Expression.Equal(foreignKey, Expression.Constant(null, foreignKey.Type)));
        }
    }
}

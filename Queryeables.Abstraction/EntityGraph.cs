using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

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
/// A parent entity and its child collections, saved, removed and loaded together through any <see cref="IEntityStore"/>
/// (Dommel, EF Core...). Immutable once configured: build it once and share it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="SaveAsync"/> inserts or updates the parent, writes its key into the children's foreign key,
/// then aligns the stored children with the collection (<see cref="EntityStoreExtensions.SyncChildrenAsync{TChild}"/>).
/// A null collection means "not loaded": the stored children are left as they are. An empty collection deletes them.</item>
/// <item><see cref="RemoveAsync"/> deletes the children, then the parent.</item>
/// <item><see cref="LoadAsync(IEntityStore, TParent, CancellationToken)"/> reads the children of a parent into its collection.</item>
/// </list>
/// Save and remove must run in a transaction, for example inside
/// <see cref="EntityStoreExtensions.RunInTransactionAsync(IEntityStore, Func{IEntityStore, Task}, CancellationToken)"/>,
/// or through <see cref="EntityGraphService{TParent, TKey}"/>.
/// </remarks>
public sealed class EntityGraph<[DynamicallyAccessedMembers(EntityMembers.Required)] TParent, TKey> : IEntityGraph<TParent>
    where TParent : class
    where TKey : notnull
{
    private readonly Func<TParent, TKey> _key;
    private readonly List<IRelation> _relations = [];

    internal EntityGraph(Expression<Func<TParent, TKey>> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _key = key.Compile();
    }

    /// <summary>Key of the parent.</summary>
    public TKey KeyOf(TParent parent) => _key(parent);

    /// <summary>True when the parent's key still has its default value: it is not in the database yet.</summary>
    public bool IsNew(TParent parent) => IsDefault(_key(parent));

    /// <summary>Declares a child collection.</summary>
    /// <param name="children">Collection property of the parent, for example <c>p => p.Contacts</c>. It must be writable.</param>
    /// <param name="foreignKey">Child property holding the parent's key, for example <c>c => c.IdPerson</c>. It must be writable.</param>
    /// <param name="key">Primary key of the child: a default value means a new child; equal keys mean the same row.</param>
    /// <param name="onLoaded">
    /// Called after <see cref="LoadAsync(IEntityStore, TParent, CancellationToken)"/> with the parent and its loaded children, for example to set navigation properties
    /// (the generated <c>{Parent}Graph</c> class does it).
    /// </param>
    public EntityGraph<TParent, TKey> HasMany<[DynamicallyAccessedMembers(EntityMembers.Required)] TChild, TChildKey>(
        Expression<Func<TParent, IEnumerable<TChild>?>> children,
        Expression<Func<TChild, TKey>> foreignKey,
        Expression<Func<TChild, TChildKey>> key,
        Action<TParent, IReadOnlyList<TChild>>? onLoaded = null)
        where TChild : class
        where TChildKey : notnull
    {
        _relations.Add(new Relation<TChild, TChildKey>(children, foreignKey, key, onLoaded));
        return this;
    }

    /// <summary>
    /// Inserts or updates the parent and aligns its children. Throws <see cref="EntityStoreException"/>
    /// (NotFound) when an updated parent no longer exists. Run it in a transaction.
    /// </summary>
    public async Task SaveAsync(IEntityStore store, TParent parent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(parent);

        await store.SaveAsync(parent, IsNew, ct);   // a generated key is written back into the parent
        var key = _key(parent);
        foreach (var relation in _relations)
            await relation.SaveAsync(store, parent, key, ct);
    }

    /// <summary>
    /// Deletes the children stored for the parent, then the parent. Throws <see cref="EntityStoreException"/>
    /// (NotFound) when the parent no longer exists. Run it in a transaction.
    /// </summary>
    public async Task RemoveAsync(IEntityStore store, TParent parent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(parent);

        if (!IsNew(parent))
        {
            var key = _key(parent);
            foreach (var relation in _relations)
                await relation.RemoveAsync(store, key, ct);
        }

        if (!await store.DeleteAsync(parent, ct))
            throw new EntityStoreException(EntityErrorCode.NotFound, $"{typeof(TParent).Name} not found: nothing was deleted.");
    }

    /// <summary>Reads the children of the parent into its collections. A new parent gets empty collections.</summary>
    public async Task LoadAsync(IEntityStore store, TParent parent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(parent);

        var isNew = IsNew(parent);
        var key = _key(parent);
        foreach (var relation in _relations)
            await relation.LoadAsync(store, parent, isNew ? default : key, ct);
    }

    /// <summary>Reads one child collection of the parent, for example <c>nameof(Person.Contacts)</c>.</summary>
    /// <exception cref="ArgumentException">No collection of that name was declared with <see cref="HasMany"/>.</exception>
    public Task LoadAsync(IEntityStore store, TParent parent, string collection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(collection);

        var relation = _relations.Find(r => r.Name == collection)
            ?? throw new ArgumentException($"{typeof(TParent).Name} has no child collection named '{collection}'.", nameof(collection));
        return relation.LoadAsync(store, parent, IsNew(parent) ? default : _key(parent), ct);
    }

    private static bool IsDefault<T>(T value) => value is null || EqualityComparer<T>.Default.Equals(value, default!);

    private interface IRelation
    {
        string Name { get; }
        Task SaveAsync(IEntityStore store, TParent parent, TKey key, CancellationToken ct);
        Task RemoveAsync(IEntityStore store, TKey key, CancellationToken ct);
        Task LoadAsync(IEntityStore store, TParent parent, TKey? key, CancellationToken ct);
    }

    private sealed class Relation<[DynamicallyAccessedMembers(EntityMembers.Required)] TChild, TChildKey> : IRelation
        where TChild : class
        where TChildKey : notnull
    {
        private readonly Func<TParent, IEnumerable<TChild>?> _getChildren;
        private readonly Action<TParent, List<TChild>> _setChildren;
        private readonly Expression<Func<TChild, TKey>> _foreignKey;
        private readonly Action<TChild, TKey> _setForeignKey;
        private readonly Func<TChild, TChildKey> _childKey;
        private readonly Action<TParent, IReadOnlyList<TChild>>? _onLoaded;

        public Relation(Expression<Func<TParent, IEnumerable<TChild>?>> children, Expression<Func<TChild, TKey>> foreignKey,
            Expression<Func<TChild, TChildKey>> key, Action<TParent, IReadOnlyList<TChild>>? onLoaded)
        {
            ArgumentNullException.ThrowIfNull(children);
            ArgumentNullException.ThrowIfNull(foreignKey);
            ArgumentNullException.ThrowIfNull(key);

            _getChildren = children.Compile();
            _setChildren = Setter<TParent, List<TChild>>(children, nameof(children), out var property);
            Name = property.Name;
            _foreignKey = foreignKey;
            _setForeignKey = Setter<TChild, TKey>(foreignKey, nameof(foreignKey), out _);
            _childKey = key.Compile();
            _onLoaded = onLoaded;
        }

        public string Name { get; }

        public async Task SaveAsync(IEntityStore store, TParent parent, TKey key, CancellationToken ct)
        {
            // Null: the children were not loaded, they stay as they are. Deleting them would lose data.
            if (_getChildren(parent)?.ToList() is not { } current)
                return;

            var before = await store.SelectAsync(ForeignKeyEquals(key), ct);

            // An existing child must already belong to this parent: otherwise the update would move a row of another
            // parent here (a remote client could take over any child by its key).
            var owned = before.Select(_childKey).ToHashSet();
            foreach (var child in current)
            {
                if (!IsDefault(_childKey(child)) && !owned.Contains(_childKey(child)))
                    throw new EntityStoreException(EntityErrorCode.Conflict,
                        $"{typeof(TChild).Name} {_childKey(child)} does not belong to this {typeof(TParent).Name}.");
            }

            foreach (var child in current)
                _setForeignKey(child, key);

            await store.SyncChildrenAsync(before, current,
                child => IsDefault(_childKey(child)),
                (a, b) => EqualityComparer<TChildKey>.Default.Equals(_childKey(a), _childKey(b)),
                ct);
        }

        public async Task RemoveAsync(IEntityStore store, TKey key, CancellationToken ct)
        {
            foreach (var child in await store.SelectAsync(ForeignKeyEquals(key), ct))
                await store.DeleteAsync(child, ct);
        }

        public async Task LoadAsync(IEntityStore store, TParent parent, TKey? key, CancellationToken ct)
        {
            var children = key is null || IsDefault(key)
                ? []
                : (await store.SelectAsync(ForeignKeyEquals(key), ct)).ToList();
            _setChildren(parent, children);
            _onLoaded?.Invoke(parent, children);
        }

        /// <summary><c>c => c.ForeignKey == key</c>: translatable by Dommel, EF and the remote filters.</summary>
        private Expression<Func<TChild, bool>> ForeignKeyEquals(TKey key)
            => Expression.Lambda<Func<TChild, bool>>(
                Expression.Equal(_foreignKey.Body, Expression.Constant(key, typeof(TKey))),
                _foreignKey.Parameters);
    }

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
}

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Queryeables.Abstraction;

/// <summary>
/// An entity type an application declares, with its key: what the source generator registers for every entity it knows
/// (<c>[EntityCache]</c>, the parents and children of <c>[EntityGraph]</c>, the targets of <c>[EntityReference]</c>). Read
/// back by what applies to every entity: exposing them on a server, copying them offline, creating their tables.
/// </summary>
public interface IEntityType
{
    /// <summary>The entity type.</summary>
    Type Type { get; }

    /// <summary>The type of its key.</summary>
    Type KeyType { get; }

    /// <summary>Calls <paramref name="visitor"/> with the type and its key as type arguments.</summary>
    void Accept(IEntityTypeVisitor visitor);
}

/// <summary>Does something typed for each <see cref="IEntityType"/> (no reflection: the types are type arguments).</summary>
public interface IEntityTypeVisitor
{
    void Visit<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TKey>(EntityType<TEntity, TKey> type)
        where TEntity : class
        where TKey : notnull;
}

/// <summary>An entity type and its key.</summary>
public sealed class EntityType<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TKey> : IEntityType
    where TEntity : class
    where TKey : notnull
{
    public EntityType(Expression<Func<TEntity, TKey>> key) => Key = key ?? throw new ArgumentNullException(nameof(key));

    /// <summary>The key, as the stores and the offline mode take it.</summary>
    public Expression<Func<TEntity, TKey>> Key { get; }

    public Type Type => typeof(TEntity);

    public Type KeyType => typeof(TKey);

    public void Accept(IEntityTypeVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        visitor.Visit(this);
    }
}

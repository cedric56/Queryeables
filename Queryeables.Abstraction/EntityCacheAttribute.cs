namespace Queryeables.Abstraction;

/// <summary>
/// Gives the class an in-memory cache, <see cref="IEntityCache{TEntity, TKey}"/>, kept up to date by its change feed.
/// <c>AddGeneratedEntities()</c> registers both. The key is the <c>Id</c> property, or the one marked <c>[Key]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EntityCacheAttribute : Attribute
{
    /// <summary>
    /// Load the cache when the application starts (a hosted service, or <c>StartQueryeablesAsync</c> without a host),
    /// rather than on the first <c>LoadAsync</c> of the application.
    /// </summary>
    public bool Preload { get; set; }
}

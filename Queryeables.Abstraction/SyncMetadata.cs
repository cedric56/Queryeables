using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Abstraction;

/// <summary>
/// Last modification date, kept up to date by <see cref="SyncConventionsStore"/>.
/// Enables incremental synchronization: a client only fetches what changed since its last synchronization.
/// The property must be a <see cref="DateTime"/> (UTC) or a <see cref="DateTimeOffset"/>.
/// </summary>
/// <remarks>Idea taken from Datasync Community Toolkit (UpdatedAt), see CREDITS.md.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class LastModifiedAttribute : Attribute;

/// <summary>
/// Soft delete: <see cref="SyncConventionsStore"/> sets this boolean to true instead of deleting the row,
/// and hides it from reads. Synchronizing clients learn about the deletion this way.
/// </summary>
/// <remarks>Idea taken from Datasync Community Toolkit (Deleted), see CREDITS.md.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class SoftDeleteAttribute : Attribute;

/// <summary>Access to the [LastModified] and [SoftDelete] properties of an entity type.</summary>
public static class SyncMetadata
{
    private sealed record Properties(PropertyInfo? LastModified, PropertyInfo? SoftDelete);

    private static readonly ConcurrentDictionary<Type, Properties> _cache = new();

    public static PropertyInfo? LastModified([DynamicallyAccessedMembers(EntityMembers.Required)] Type type) => For(type).LastModified;

    public static PropertyInfo? SoftDelete([DynamicallyAccessedMembers(EntityMembers.Required)] Type type) => For(type).SoftDelete;

    /// <summary>Sets the modification date to now (no effect if the type has none).</summary>
    public static void Touch<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T entity, DateTime utcNow)
    {
        if (LastModified(typeof(T)) is not { } property || entity is null)
            return;

        property.SetValue(entity, property.PropertyType == typeof(DateTimeOffset)
            ? (object)new DateTimeOffset(utcNow, TimeSpan.Zero)
            : (object)DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }

    /// <summary>Modification date in UTC, or null if the type has none.</summary>
    public static DateTime? LastModifiedUtc<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T entity)
        => entity is null ? null : LastModified(typeof(T))?.GetValue(entity) switch
        {
            DateTimeOffset dto => dto.UtcDateTime,
            DateTime dt => dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            _ => null
        };

    public static bool IsDeleted<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T entity)
        => entity is not null && SoftDelete(typeof(T))?.GetValue(entity) is true;

    public static void SetDeleted<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T entity, bool deleted)
    {
        if (entity is not null)
            SoftDelete(typeof(T))?.SetValue(entity, deleted);
    }

    /// <summary>Adds "not deleted" to the predicate when the type uses soft delete.</summary>
    public static Expression<Func<T, bool>> NotDeleted<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(
        Expression<Func<T, bool>>? predicate)
    {
        if (SoftDelete(typeof(T)) is not { } property)
            return predicate ?? (_ => true);

        var parameter = predicate?.Parameters[0] ?? Expression.Parameter(typeof(T), "e");
        // "== false" rather than "!": the form understood by Dommel, EF and remote filters.
        Expression notDeleted = Expression.Equal(Expression.Property(parameter, property), Expression.Constant(false));
        var body = predicate is null ? notDeleted : Expression.AndAlso(predicate.Body, notDeleted);
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    /// <summary>
    /// <c>e => e.LastModified &gt;= since</c>. "&gt;=" rather than "&gt;": two changes within the same millisecond
    /// are never lost; a row already received may come back, which is harmless for a cache.
    /// </summary>
    public static Expression<Func<T, bool>> ModifiedSince<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(DateTime sinceUtc)
    {
        var property = LastModified(typeof(T))
            ?? throw new NotSupportedException(
                $"{typeof(T).Name} n'a pas de propriété [LastModified] : la synchronisation incrémentale est impossible.");

        var parameter = Expression.Parameter(typeof(T), "e");
        var since = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc);
        var constant = property.PropertyType == typeof(DateTimeOffset)
            ? Expression.Constant(new DateTimeOffset(since, TimeSpan.Zero))
            : Expression.Constant(since);
        var body = Expression.GreaterThanOrEqual(Expression.Property(parameter, property), constant);
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private static Properties For([DynamicallyAccessedMembers(EntityMembers.Required)] Type type)
    {
        if (_cache.TryGetValue(type, out var cached))
            return cached;

        var all = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var lastModified = all.SingleOrDefault(p => p.GetCustomAttribute<LastModifiedAttribute>() is not null);
        if (lastModified is not null && lastModified.PropertyType != typeof(DateTime) && lastModified.PropertyType != typeof(DateTimeOffset))
            throw new NotSupportedException($"{type.Name}.{lastModified.Name} : [LastModified] attend un DateTime ou un DateTimeOffset.");

        var softDelete = all.SingleOrDefault(p => p.GetCustomAttribute<SoftDeleteAttribute>() is not null);
        if (softDelete is not null && softDelete.PropertyType != typeof(bool))
            throw new NotSupportedException($"{type.Name}.{softDelete.Name} : [SoftDelete] attend un bool.");

        var properties = new Properties(lastModified, softDelete);
        _cache.TryAdd(type, properties);
        return properties;
    }
}

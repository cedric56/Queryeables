using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Abstraction;

/// <summary>
/// Date de dernière modification, tenue à jour par <see cref="SyncConventionsStore"/>.
/// Permet la synchronisation incrémentale : un client ne récupère que ce qui a changé depuis sa dernière synchronisation.
/// La propriété doit être un <see cref="DateTime"/> (UTC) ou un <see cref="DateTimeOffset"/>.
/// </summary>
/// <remarks>Idée reprise de Datasync Community Toolkit (UpdatedAt), voir CREDITS.md.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class LastModifiedAttribute : Attribute;

/// <summary>
/// Suppression logique : <see cref="SyncConventionsStore"/> met ce booléen à true au lieu de supprimer la ligne,
/// et l'exclut des lectures. Les clients qui se synchronisent apprennent ainsi la suppression.
/// </summary>
/// <remarks>Idée reprise de Datasync Community Toolkit (Deleted), voir CREDITS.md.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class SoftDeleteAttribute : Attribute;

/// <summary>Accès aux propriétés [LastModified] et [SoftDelete] d'un type d'entité.</summary>
public static class SyncMetadata
{
    private sealed record Properties(PropertyInfo? LastModified, PropertyInfo? SoftDelete);

    private static readonly ConcurrentDictionary<Type, Properties> _cache = new();

    public static PropertyInfo? LastModified(Type type) => For(type).LastModified;

    public static PropertyInfo? SoftDelete(Type type) => For(type).SoftDelete;

    /// <summary>Met la date de modification à maintenant (sans effet si le type n'en a pas).</summary>
    public static void Touch(object entity, DateTime utcNow)
    {
        if (LastModified(entity.GetType()) is not { } property)
            return;

        property.SetValue(entity, property.PropertyType == typeof(DateTimeOffset)
            ? (object)new DateTimeOffset(utcNow, TimeSpan.Zero)
            : (object)DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }

    /// <summary>Date de modification en UTC, ou null si le type n'en a pas.</summary>
    public static DateTime? LastModifiedUtc(object entity)
        => LastModified(entity.GetType())?.GetValue(entity) switch
        {
            DateTimeOffset dto => dto.UtcDateTime,
            DateTime dt => dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            _ => null
        };

    public static bool IsDeleted(object entity)
        => SoftDelete(entity.GetType())?.GetValue(entity) is true;

    public static void SetDeleted(object entity, bool deleted)
        => SoftDelete(entity.GetType())?.SetValue(entity, deleted);

    /// <summary>Ajoute « non supprimé » au prédicat, si le type a une suppression logique.</summary>
    public static Expression<Func<T, bool>> NotDeleted<T>(Expression<Func<T, bool>>? predicate)
    {
        if (SoftDelete(typeof(T)) is not { } property)
            return predicate ?? (_ => true);

        var parameter = predicate?.Parameters[0] ?? Expression.Parameter(typeof(T), "e");
        // « == false » plutôt que « ! » : forme comprise par Dommel, EF et les filtres distants.
        Expression notDeleted = Expression.Equal(Expression.Property(parameter, property), Expression.Constant(false));
        var body = predicate is null ? notDeleted : Expression.AndAlso(predicate.Body, notDeleted);
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    /// <summary>
    /// <c>e => e.LastModified &gt;= since</c>. « &gt;= » plutôt que « &gt; » : deux modifications de la même milliseconde
    /// ne sont jamais perdues ; une ligne déjà reçue peut revenir, ce qui est sans effet sur un cache.
    /// </summary>
    public static Expression<Func<T, bool>> ModifiedSince<T>(DateTime sinceUtc)
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

    private static Properties For(Type type) => _cache.GetOrAdd(type, static t =>
    {
        var all = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var lastModified = all.SingleOrDefault(p => p.GetCustomAttribute<LastModifiedAttribute>() is not null);
        if (lastModified is not null && lastModified.PropertyType != typeof(DateTime) && lastModified.PropertyType != typeof(DateTimeOffset))
            throw new NotSupportedException($"{t.Name}.{lastModified.Name} : [LastModified] attend un DateTime ou un DateTimeOffset.");

        var softDelete = all.SingleOrDefault(p => p.GetCustomAttribute<SoftDeleteAttribute>() is not null);
        if (softDelete is not null && softDelete.PropertyType != typeof(bool))
            throw new NotSupportedException($"{t.Name}.{softDelete.Name} : [SoftDelete] attend un bool.");

        return new Properties(lastModified, softDelete);
    });
}

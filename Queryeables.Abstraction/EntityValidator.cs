using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Abstraction;

/// <summary>Validation with DataAnnotations attributes ([Required], [StringLength]...).</summary>
/// <remarks>
/// DataAnnotations validation reads the entity's properties and attributes through reflection. The entity type is
/// annotated with <see cref="EntityMembers.Required"/>, which keeps its public properties (and their attributes)
/// when trimming: the trimming warnings of <see cref="Validator"/> are therefore suppressed with that justification.
/// </remarks>
public static class EntityValidator
{
    private const string Justification =
        "The validated type is annotated with DynamicallyAccessedMembers(PublicProperties): its properties and their attributes are kept.";

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = Justification)]
    public static IReadOnlyList<ValidationResult> Validate<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var context = new ValidationContext(instance, typeof(T).Name, serviceProvider: null, items: null);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, context, results, validateAllProperties: true);
        return results;
    }

    /// <summary>
    /// The entity and the children of its [EntityGraph] collections, at every level: a valid parent does not carry invalid
    /// children into the database. A child's messages start with its place, such as <c>Contacts[2]: The Name field is required.</c>
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = Justification)]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "The children of an [EntityGraph] collection are entity types themselves, annotated where they are registered.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "The children of an [EntityGraph] collection are entity types themselves, annotated where they are registered.")]
    public static IReadOnlyList<ValidationResult> ValidateGraph<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var results = new List<ValidationResult>(Validate(instance));
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance) { instance };
        Children(instance, typeof(T), string.Empty, depth: 1);
        return results;

        void Children(object parent, Type type, string path, int depth)
        {
            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!property.IsDefined(typeof(EntityGraphAttribute), inherit: true)
                    || property.GetValue(parent) is not System.Collections.IEnumerable children || children is string)
                    continue;
                if (depth > MaxGraphDepth)
                    throw new ArgumentException($"A graph is limited to {MaxGraphDepth} levels.");

                var index = 0;
                foreach (var child in children)
                {
                    var place = $"{path}{property.Name}[{index++}]";
                    if (child is null || !seen.Add(child))
                        continue;   // a child reached twice (a cycle) is validated once
                    var context = new ValidationContext(child, child.GetType().Name, serviceProvider: null, items: null);
                    var own = new List<ValidationResult>();
                    Validator.TryValidateObject(child, context, own, validateAllProperties: true);
                    results.AddRange(own.Select(r => new ValidationResult($"{place}: {r.ErrorMessage}",
                        r.MemberNames.Select(m => $"{place}.{m}"))));
                    Children(child, child.GetType(), place + ".", depth + 1);
                }
            }
        }
    }

    /// <summary>Most levels of children <see cref="ValidateGraph{T}"/> follows.</summary>
    public const int MaxGraphDepth = 8;

    public static string? Error<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(T instance)
    {
        var errors = Validate(instance);
        return errors.Count == 0
            ? null
            : string.Join(Environment.NewLine, errors.Select(e => e.ErrorMessage));
    }

    /// <summary>Validates a single property, or the whole entity when <paramref name="propertyName"/> is empty.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = Justification)]
    public static IEnumerable<ValidationResult> ValidateProperty<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(
        T instance, string? propertyName)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (string.IsNullOrWhiteSpace(propertyName))
            return Validate(instance);

        var property = typeof(T).GetProperty(propertyName)
            ?? throw new ArgumentException($"Unknown property {propertyName}", nameof(propertyName));
        var context = new ValidationContext(instance, typeof(T).Name, serviceProvider: null, items: null) { MemberName = propertyName };
        var results = new List<ValidationResult>();
        Validator.TryValidateProperty(property.GetValue(instance), context, results);
        return results;
    }

    public static IEnumerable<string> ValidateErrorsProperty<[DynamicallyAccessedMembers(EntityMembers.Required)] T>(
        T instance, string? propertyName)
        => ValidateProperty(instance, propertyName)
            .Select(e => e.ErrorMessage ?? string.Empty)
            .Where(message => !string.IsNullOrEmpty(message));
}

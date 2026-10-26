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

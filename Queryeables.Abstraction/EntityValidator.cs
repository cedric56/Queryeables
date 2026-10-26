using System.ComponentModel.DataAnnotations;

namespace Queryeables.Abstraction;

public static class EntityValidator
{
    public static IReadOnlyList<ValidationResult> Validate<T>(T instance)
    {
        var ctx = new ValidationContext(instance!);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance!, ctx, results, validateAllProperties: true);
        return results;
    }

    public static string? Error<T>(T instance)
    {
        var errors = Validate(instance);
        return errors.Count == 0
            ? null
            : string.Join(Environment.NewLine, errors.Select(e => e.ErrorMessage));
    }

    /// <summary>Équivalent de ton Validate(source, columnName).</summary>
    public static IEnumerable<ValidationResult> ValidateProperty<T>(T instance, string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return Validate(instance);

        var prop = typeof(T).GetProperty(propertyName)
            ?? throw new ArgumentException($"Unknown property {propertyName}", nameof(propertyName));
        var value = prop.GetValue(instance);
        var ctx = new ValidationContext(instance!) { MemberName = propertyName };
        var results = new List<ValidationResult>();
        Validator.TryValidateProperty(value, ctx, results);

        return results ?? Enumerable.Empty<ValidationResult>();
    }

    public static IEnumerable<string> ValidateErrorsProperty<T>(T instance, string? propertyName)
    {
        var results = ValidateProperty(instance, propertyName);
        return results.Select(e => e.ErrorMessage ?? string.Empty).Where(msg => !string.IsNullOrEmpty(msg));
    }
}
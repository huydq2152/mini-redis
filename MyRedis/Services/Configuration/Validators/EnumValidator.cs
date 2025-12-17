using MyRedis.Abstractions.Configuration;

namespace MyRedis.Services.Configuration.Validators;

/// <summary>
/// Enum validator - validates against allowed values.
/// Case-insensitive.
/// </summary>
public class EnumValidator : IParameterValidator
{
    private readonly HashSet<string> _allowedValues;

    public EnumValidator(params string[] allowedValues)
    {
        if (allowedValues == null || allowedValues.Length == 0)
        {
            throw new ArgumentException("At least one allowed value must be specified", nameof(allowedValues));
        }

        _allowedValues = new HashSet<string>(
            allowedValues,
            StringComparer.OrdinalIgnoreCase);
    }

    public ValidationResult Validate(string value)
    {
        if (_allowedValues.Contains(value))
        {
            return ValidationResult.Success();
        }

        return ValidationResult.Failure(
            $"Invalid value '{value}'. Must be one of: {string.Join(", ", _allowedValues)}");
    }

    public string GetValidationDescription()
    {
        return string.Join("|", _allowedValues);
    }
}

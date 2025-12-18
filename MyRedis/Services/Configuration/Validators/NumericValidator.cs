using MyRedis.Abstractions.Configuration;

namespace MyRedis.Services.Configuration.Validators;

/// <summary>
/// Numeric validator with range constraints and unit support.
/// Supports: 100, 1kb, 1mb, 1gb
/// </summary>
public class NumericValidator(long min = long.MinValue, long max = long.MaxValue, bool allowUnits = false)
    : IParameterValidator
{
    public ValidationResult Validate(string value)
    {
        long parsedValue;

        if (allowUnits)
        {
            // Parse with units: 1kb, 1mb, 1gb
            if (!TryParseWithUnits(value, out parsedValue))
            {
                return ValidationResult.Failure(
                    $"Invalid numeric value '{value}'. Expected integer or value with units (kb, mb, gb).");
            }
        }
        else
        {
            // Parse as plain integer
            if (!long.TryParse(value, out parsedValue))
            {
                return ValidationResult.Failure($"Invalid integer value '{value}'.");
            }
        }

        // Range check
        if (parsedValue < min || parsedValue > max)
        {
            return ValidationResult.Failure(
                $"Value {parsedValue} out of range. Must be between {min} and {max}.");
        }

        return ValidationResult.Success();
    }

    public string GetValidationDescription()
    {
        var desc = $"integer between {min} and {max}";
        if (allowUnits) desc += " (supports kb/mb/gb units)";
        return desc;
    }

    private bool TryParseWithUnits(string value, out long result)
    {
        value = value.Trim().ToLower();
        long multiplier = 1;

        if (value.EndsWith("gb"))
        {
            multiplier = 1024 * 1024 * 1024;
            value = value[..^2];
        }
        else if (value.EndsWith("mb"))
        {
            multiplier = 1024 * 1024;
            value = value[..^2];
        }
        else if (value.EndsWith("kb"))
        {
            multiplier = 1024;
            value = value[..^2];
        }

        if (long.TryParse(value.Trim(), out var baseValue))
        {
            result = baseValue * multiplier;
            return true;
        }

        result = 0;
        return false;
    }
}

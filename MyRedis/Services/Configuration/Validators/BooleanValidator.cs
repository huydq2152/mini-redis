using MyRedis.Abstractions.Configuration;

namespace MyRedis.Services.Configuration.Validators;

/// <summary>
/// Boolean validator - accepts yes/no, true/false, 1/0.
/// </summary>
public class BooleanValidator : IParameterValidator
{
    public ValidationResult Validate(string value)
    {
        var normalized = value.ToLower();
        if (normalized is "yes" or "no" or "true" or "false" or "1" or "0")
        {
            return ValidationResult.Success();
        }

        return ValidationResult.Failure(
            $"Invalid boolean value '{value}'. Use yes/no, true/false, or 1/0.");
    }

    public string GetValidationDescription() => "yes|no";
}

namespace MyRedis.Abstractions.Configuration;

/// <summary>
/// Validator interface for parameter values.
///
/// Design Pattern: Strategy Pattern
/// - Each parameter has a validator
/// - Validators are composable (ChainValidator)
/// - Easy to add new validation logic
/// - Testable in isolation
/// </summary>
public interface IParameterValidator
{
    /// <summary>
    /// Validates a parameter value.
    /// Returns success or validation error with friendly message.
    /// </summary>
    ValidationResult Validate(string value);

    /// <summary>
    /// Gets a description of valid values (for error messages).
    /// Example: "integer between 1 and 1000" or "yes|no"
    /// </summary>
    string GetValidationDescription();
}

/// <summary>
/// Validation result.
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; }
    public string? ErrorMessage { get; }

    private ValidationResult(bool isValid, string? errorMessage)
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
    }

    public static ValidationResult Success() => new(true, null);
    public static ValidationResult Failure(string message) => new(false, message);
}

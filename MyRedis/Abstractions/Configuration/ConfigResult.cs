namespace MyRedis.Abstractions.Configuration;

/// <summary>
/// Result of a configuration SET operation.
/// Provides success/failure status and error message.
/// </summary>
public class ConfigResult
{
    public bool Success { get; }
    public string? ErrorMessage { get; }

    private ConfigResult(bool success, string? errorMessage)
    {
        Success = success;
        ErrorMessage = errorMessage;
    }

    public static ConfigResult Ok() => new(true, null);
    public static ConfigResult Failure(string message) => new(false, message);
}

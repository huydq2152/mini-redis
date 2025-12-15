namespace MyRedis.CLI.Domain;

/// <summary>
/// Interface for loading Redis commands from files.
/// Supports different file formats and error handling strategies.
/// </summary>
public interface IFileCommandLoader
{
    /// <summary>
    /// Loads commands from a file.
    /// </summary>
    /// <param name="filePath">The path to the file containing commands</param>
    /// <returns>A collection of parsed commands</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file doesn't exist</exception>
    /// <exception cref="InvalidOperationException">Thrown when the file format is invalid</exception>
    Task<IReadOnlyList<RedisCommand>> LoadCommandsAsync(string filePath);
    
    /// <summary>
    /// Tries to load commands from a file.
    /// </summary>
    /// <param name="filePath">The path to the file containing commands</param>
    /// <returns>A result containing the commands or error information</returns>
    Task<FileLoadResult> TryLoadCommandsAsync(string filePath);
}

/// <summary>
/// Represents the result of a file loading operation.
/// </summary>
public sealed class FileLoadResult
{
    public bool Success { get; }
    public IReadOnlyList<RedisCommand> Commands { get; }
    public string? ErrorMessage { get; }
    
    private FileLoadResult(bool success, IReadOnlyList<RedisCommand> commands, string? errorMessage)
    {
        Success = success;
        Commands = commands;
        ErrorMessage = errorMessage;
    }
    
    public static FileLoadResult Successful(IReadOnlyList<RedisCommand> commands) 
        => new(true, commands, null);
        
    public static FileLoadResult Failed(string errorMessage) 
        => new(false, Array.Empty<RedisCommand>(), errorMessage);
}
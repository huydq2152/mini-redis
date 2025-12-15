using MyRedis.CLI.Domain;

namespace MyRedis.CLI.Application;

/// <summary>
/// Application service for loading Redis commands from files.
/// Supports line-by-line command parsing with error recovery.
/// </summary>
public sealed class FileCommandLoader : IFileCommandLoader
{
    private readonly ICommandParser _commandParser;

    public FileCommandLoader(ICommandParser commandParser)
    {
        _commandParser = commandParser ?? throw new ArgumentNullException(nameof(commandParser));
    }

    public async Task<IReadOnlyList<RedisCommand>> LoadCommandsAsync(string filePath)
    {
        var result = await TryLoadCommandsAsync(filePath);
        
        if (!result.Success)
            throw new InvalidOperationException(result.ErrorMessage);
            
        return result.Commands;
    }

    public async Task<FileLoadResult> TryLoadCommandsAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return FileLoadResult.Failed($"File not found: {filePath}");

            var lines = await File.ReadAllLinesAsync(filePath);
            var commands = new List<RedisCommand>();
            var errors = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                
                // Skip empty lines and comments
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;

                if (_commandParser.TryParseCommand(line, out var command) && command != null)
                {
                    commands.Add(command);
                }
                else
                {
                    errors.Add($"Line {i + 1}: Invalid command '{line}'");
                }
            }

            // Return partial success if we have some commands, even with errors
            if (commands.Count > 0 && errors.Count == 0)
                return FileLoadResult.Successful(commands);
            else if (commands.Count > 0)
                return FileLoadResult.Failed($"Loaded {commands.Count} commands with {errors.Count} errors: {string.Join("; ", errors)}");
            else
                return FileLoadResult.Failed($"No valid commands found. Errors: {string.Join("; ", errors)}");
        }
        catch (Exception ex)
        {
            return FileLoadResult.Failed($"Error reading file: {ex.Message}");
        }
    }
}
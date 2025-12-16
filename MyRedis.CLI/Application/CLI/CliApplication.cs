using MyRedis.CLI.Application.Services;
using MyRedis.CLI.Domain.Commands;

namespace MyRedis.CLI.Application.CLI;

/// <summary>
/// Main CLI application orchestrator that coordinates all application services.
/// Implements the use cases for the Redis CLI application including REPL,
/// file execution, and pipeline operations.
/// </summary>
public sealed class CliApplication : IDisposable
{
    private readonly RedisClientService _redisClient;
    private readonly ICommandParser _commandParser;
    private readonly IFileCommandLoader _fileLoader;
    private bool _disposed;

    public CliApplication(
        RedisClientService redisClient,
        ICommandParser commandParser,
        IFileCommandLoader fileLoader)
    {
        _redisClient = redisClient ?? throw new ArgumentNullException(nameof(redisClient));
        _commandParser = commandParser ?? throw new ArgumentNullException(nameof(commandParser));
        _fileLoader = fileLoader ?? throw new ArgumentNullException(nameof(fileLoader));
    }

    /// <summary>
    /// Starts the interactive REPL (Read-Eval-Print Loop) session.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for graceful shutdown</param>
    public async Task RunInteractiveAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CliApplication));

        try
        {
            await _redisClient.ConnectAsync();
            Console.WriteLine($"Connected to Redis at {_redisClient.Settings}");
            Console.WriteLine("Type 'exit' or 'quit' to close the connection.");
            Console.WriteLine("Type 'help' for available commands.");
            Console.WriteLine();

            while (!cancellationToken.IsCancellationRequested)
            {
                Console.Write($"{_redisClient.Settings}> ");
                var input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                if (IsExitCommand(input))
                    break;

                if (IsHelpCommand(input))
                {
                    ShowHelp();
                    continue;
                }

                await ProcessSingleCommandAsync(input);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Connection error: {ex.Message}");
        }
        finally
        {
            _redisClient.Disconnect();
            Console.WriteLine("Disconnected from Redis.");
        }
    }

    /// <summary>
    /// Executes commands from a file.
    /// </summary>
    /// <param name="filePath">Path to the file containing Redis commands</param>
    /// <param name="usePipeline">Whether to execute commands as a pipeline</param>
    public async Task ExecuteFileAsync(string filePath, bool usePipeline = false)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CliApplication));

        try
        {
            await _redisClient.ConnectAsync();
            Console.WriteLine($"Connected to Redis at {_redisClient.Settings}");
            
            var loadResult = await _fileLoader.TryLoadCommandsAsync(filePath);
            if (!loadResult.Success)
            {
                Console.WriteLine($"Error loading file: {loadResult.ErrorMessage}");
                return;
            }

            var commands = loadResult.Commands;
            Console.WriteLine($"Loaded {commands.Count} commands from {filePath}");

            if (usePipeline)
            {
                await ExecutePipelineAsync(commands);
            }
            else
            {
                await ExecuteSequentialAsync(commands);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Execution error: {ex.Message}");
        }
        finally
        {
            _redisClient.Disconnect();
        }
    }

    private async Task ProcessSingleCommandAsync(string input)
    {
        try
        {
            if (!_commandParser.TryParseCommand(input, out var command) || command == null)
            {
                Console.WriteLine("(error) Invalid command syntax");
                return;
            }

            var response = await _redisClient.ExecuteCommandAsync(command);
            Console.WriteLine(response.Format());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(error) {ex.Message}");
        }
    }

    private async Task ExecuteSequentialAsync(IReadOnlyList<RedisCommand> commands)
    {
        for (int i = 0; i < commands.Count; i++)
        {
            try
            {
                var command = commands[i];
                Console.WriteLine($"[{i + 1}/{commands.Count}] {command.OriginalText}");
                
                var response = await _redisClient.ExecuteCommandAsync(command);
                Console.WriteLine(response.Format());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"(error) {ex.Message}");
            }
        }
    }

    private async Task ExecutePipelineAsync(IReadOnlyList<RedisCommand> commands)
    {
        try
        {
            Console.WriteLine($"Executing {commands.Count} commands in pipeline...");
            
            var responses = await _redisClient.ExecutePipelineAsync(commands);
            
            for (int i = 0; i < commands.Count; i++)
            {
                Console.WriteLine($"[{i + 1}] {commands[i].OriginalText}");
                Console.WriteLine(responses[i].Format());
                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(error) Pipeline execution failed: {ex.Message}");
        }
    }

    private static bool IsExitCommand(string input)
    {
        var normalized = input.Trim().ToLowerInvariant();
        return normalized == "exit" || normalized == "quit" || normalized == "q";
    }

    private static bool IsHelpCommand(string input)
    {
        var normalized = input.Trim().ToLowerInvariant();
        return normalized == "help" || normalized == "?";
    }

    private static void ShowHelp()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("  help, ?           - Show this help message");
        Console.WriteLine("  exit, quit, q     - Exit the CLI");
        Console.WriteLine("  <redis-command>   - Execute any Redis command");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  SET key value");
        Console.WriteLine("  GET key");
        Console.WriteLine("  PING");
        Console.WriteLine("  KEYS *");
        Console.WriteLine();
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _redisClient?.Dispose();
        _disposed = true;
    }
}
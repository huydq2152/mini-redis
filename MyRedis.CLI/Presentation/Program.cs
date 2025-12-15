using MyRedis.CLI.Application;
using MyRedis.CLI.Domain;
using MyRedis.CLI.Infrastructure;

namespace MyRedis.CLI.Presentation;

/// <summary>
/// Entry point for the Redis CLI application.
/// Handles command line argument parsing and application bootstrapping.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Main entry point of the application.
    /// </summary>
    /// <param name="args">Command line arguments</param>
    /// <returns>Exit code (0 for success, non-zero for error)</returns>
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            
            if (options.ShowHelp)
            {
                ShowUsage();
                return 0;
            }

            var container = ServiceContainer.CreateDefault(options.ConnectionSettings);
            
            using var app = container.Resolve<CliApplication>();
            
            if (!string.IsNullOrEmpty(options.FilePath))
            {
                // File execution mode
                await app.ExecuteFileAsync(options.FilePath, options.UsePipeline);
            }
            else
            {
                // Interactive mode
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };
                
                await app.RunInteractiveAsync(cts.Token);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Parses command line arguments into application options.
    /// </summary>
    private static ApplicationOptions ParseArguments(string[] args)
    {
        var options = new ApplicationOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-h":
                case "--host":
                    if (i + 1 < args.Length)
                        options.Host = args[++i];
                    break;
                    
                case "-p":
                case "--port":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var port))
                        options.Port = port;
                    break;
                    
                case "-f":
                case "--file":
                    if (i + 1 < args.Length)
                        options.FilePath = args[++i];
                    break;
                    
                case "--pipeline":
                    options.UsePipeline = true;
                    break;
                    
                case "--help":
                case "-?":
                    options.ShowHelp = true;
                    break;
                    
                default:
                    // If it doesn't start with -, treat as file path
                    if (!args[i].StartsWith("-") && string.IsNullOrEmpty(options.FilePath))
                        options.FilePath = args[i];
                    break;
            }
        }

        return options;
    }

    /// <summary>
    /// Shows application usage information.
    /// </summary>
    private static void ShowUsage()
    {
        Console.WriteLine("MyRedis CLI - Interactive Redis client");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  MyRedis.CLI [options] [file]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -h, --host <host>     Redis server host (default: 127.0.0.1)");
        Console.WriteLine("  -p, --port <port>     Redis server port (default: 6379)");
        Console.WriteLine("  -f, --file <file>     Execute commands from file");
        Console.WriteLine("  --pipeline            Use pipeline mode for file execution");
        Console.WriteLine("  --help, -?            Show this help message");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  MyRedis.CLI                           # Interactive mode");
        Console.WriteLine("  MyRedis.CLI -h localhost -p 6380     # Connect to specific host/port");
        Console.WriteLine("  MyRedis.CLI commands.txt              # Execute file");
        Console.WriteLine("  MyRedis.CLI --pipeline commands.txt   # Execute file with pipeline");
    }
}

/// <summary>
/// Application configuration options parsed from command line arguments.
/// </summary>
internal sealed class ApplicationOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6379;
    public string? FilePath { get; set; }
    public bool UsePipeline { get; set; }
    public bool ShowHelp { get; set; }

    /// <summary>
    /// Creates Redis connection settings from the parsed options.
    /// </summary>
    public RedisConnectionSettings ConnectionSettings =>
        new(Host, Port);
}
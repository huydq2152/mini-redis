using MyRedis.CLI;

// ============================================================================
// MyRedis Interactive CLI (redis-cli clone)
// ============================================================================
// This program provides an interactive command-line interface for the MyRedis
// server, similar to the official redis-cli tool.
//
// Usage:
// 1. Start the MyRedis server: dotnet run --project MyRedis
// 2. Run this CLI: dotnet run --project MyRedis.CLI
// 3. Type Redis commands and press Enter
// 4. Type "quit" or "exit" to disconnect
//
// Features:
// - Interactive REPL (Read-Eval-Print-Loop)
// - Full support for all response types (nil, error, string, integer, array)
// - Proper formatting with indentation for nested arrays
// - Quoted string support for arguments with spaces
// - **Pipelining support** for high-throughput batch operations
//
// Examples (Basic):
//   127.0.0.1:6379> SET name John
//   "OK"
//   127.0.0.1:6379> GET name
//   "John"
//   127.0.0.1:6379> ZADD scores 100 Alice 85 Bob 92 Charlie
//   (integer) 3
//   127.0.0.1:6379> ZRANGE scores 0 -1
//   1) "Bob"
//   2) "Charlie"
//   3) "Alice"
//
// Examples (Pipelining):
//   127.0.0.1:6379> PIPELINE
//   Entering pipeline mode. Commands will be queued.
//   pipeline> SET key1 value1
//   Queued.
//   pipeline> SET key2 value2
//   Queued.
//   pipeline> GET key1
//   Queued.
//   pipeline> EXEC
//   Executing 3 commands...
//   1) "OK"
//   2) "OK"
//   3) "value1"
//
// Examples (File Pipeline):
//   127.0.0.1:6379> @commands.txt
//   Loading commands from commands.txt...
//   Executing 100 commands in pipeline...
//   [results...]
//
// Special Commands:
//   - PIPELINE: Enter pipeline mode (queue commands)
//   - EXEC: Execute queued commands in batch
//   - DISCARD: Cancel pipeline and discard queued commands
//   - @filename: Execute commands from file in pipeline mode
//   - quit/exit: Disconnect from server
// ============================================================================

const string host = "127.0.0.1";
const int port = 6379;

Console.WriteLine("MyRedis CLI - Interactive Client");
Console.WriteLine($"Connecting to {host}:{port}...");
Console.WriteLine();

try
{
    // Create and connect to the Redis server
    using var client = new InteractiveRedisClient(host, port);

    Console.WriteLine($"Connected to {host}:{port}");
    Console.WriteLine("Type 'quit' or 'exit' to disconnect");
    Console.WriteLine();

    // Main REPL loop
    while (true)
    {
        // Display prompt (changes based on pipeline mode)
        if (client.InPipelineMode)
        {
            Console.Write("pipeline> ");
        }
        else
        {
            Console.Write($"{host}:{port}> ");
        }

        // Read user input
        string? input = Console.ReadLine();

        // Handle empty input
        if (string.IsNullOrWhiteSpace(input))
        {
            continue;
        }

        // Trim the input
        input = input.Trim();

        // Check for exit commands
        if (input.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Goodbye!");
            break;
        }

        try
        {
            // ============================================================
            // Special Command Handling (Client-side commands)
            // ============================================================

            // PIPELINE command - Enter pipeline mode
            if (input.Equals("PIPELINE", StringComparison.OrdinalIgnoreCase))
            {
                client.EnterPipelineMode();
                Console.WriteLine();
                continue;
            }

            // EXEC command - Execute queued pipeline commands
            if (input.Equals("EXEC", StringComparison.OrdinalIgnoreCase))
            {
                client.ExecutePipeline();
                Console.WriteLine();
                continue;
            }

            // DISCARD command - Cancel pipeline mode
            if (input.Equals("DISCARD", StringComparison.OrdinalIgnoreCase))
            {
                client.DiscardPipeline();
                Console.WriteLine();
                continue;
            }

            // @filename command - Execute commands from file
            if (input.StartsWith("@"))
            {
                string fileName = input.Substring(1).Trim();
                if (string.IsNullOrEmpty(fileName))
                {
                    Console.WriteLine("Error: No filename specified. Usage: @commands.txt");
                }
                else
                {
                    client.ExecuteFromFile(fileName);
                }
                Console.WriteLine();
                continue;
            }

            // ============================================================
            // Regular Command Handling
            // ============================================================

            // Send the command to the server (or queue if in pipeline mode)
            client.SendCommand(input);

            // If in pipeline mode, command was queued - don't read response
            if (client.InPipelineMode)
            {
                // Response already printed by SendCommand() -> "Queued."
                Console.WriteLine();
                continue;
            }

            // Normal mode: Read and display the response
            string? response = client.ReadResponse();

            if (response == null)
            {
                Console.WriteLine("(connection closed by server)");
                break;
            }

            Console.WriteLine(response);
        }
        catch (Exception ex)
        {
            // Handle communication errors
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine("Connection may be lost. Try reconnecting.");
            break;
        }

        // Add blank line for readability
        Console.WriteLine();
    }
}
catch (Exception ex)
{
    // Handle connection errors
    Console.WriteLine($"Failed to connect: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the MyRedis server is running:");
    Console.WriteLine("  dotnet run --project MyRedis/MyRedis.csproj");
    Environment.Exit(1);
}

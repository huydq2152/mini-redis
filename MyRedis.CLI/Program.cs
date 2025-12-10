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
//
// Examples:
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
        // Display prompt (similar to redis-cli)
        Console.Write($"{host}:{port}> ");

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
            // Send the command to the server
            client.SendCommand(input);

            // Read and display the response
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

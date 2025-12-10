using MyRedis.Infrastructure;

// ============================================================================
// MyRedis Server - Main Entry Point
// ============================================================================
// A Redis server implementation in C# following clean architecture principles.
//
// Key Features:
// - Binary protocol parser for Redis-like commands
// - Event loop using Socket.Select() (similar to Redis)
// - Command handler pattern with dependency injection
// - Background task processing (TTL expiration, idle connections)
// - Advanced data structures (Sorted Sets with AVL trees)
//
// Architecture:
// - RedisServerOrchestrator: Main event loop coordinator
// - NetworkServer: TCP socket handling and I/O
// - CommandProcessor: Protocol parsing and command routing
// - BackgroundTaskManager: Expiration and idle connection cleanup
//
// To test the server, run the client:
//   dotnet run --project MyRedis.Client/MyRedis.Client.csproj
// ============================================================================

Console.WriteLine("Starting MyRedis Server...");
Console.WriteLine("Architecture: Clean, SOLID-compliant implementation");
Console.WriteLine("Features: Command handlers, dependency injection, separation of concerns");
Console.WriteLine("Press Ctrl+C to stop the server");

try
{
    // Set up cancellation token for graceful shutdown
    // This allows us to cleanly exit the event loop when the user presses Ctrl+C
    using var cts = new CancellationTokenSource();
    var cancelled = false;

    // Register a handler for Ctrl+C (SIGINT)
    // This is the standard way to gracefully shutdown a server application
    Console.CancelKeyPress += (_, e) =>
    {
        // Only handle the first Ctrl+C press
        // (Multiple presses could cause issues with cleanup)
        if (!cancelled)
        {
            Console.WriteLine("\n[Shutdown] Graceful shutdown initiated...");

            // Cancel the default behavior (immediate termination)
            // This gives us time to clean up resources properly
            e.Cancel = true;

            // Set our flag to prevent double-handling
            cancelled = true;

            // Signal the cancellation token, which will stop the event loop
            cts.Cancel();
        }
    };

    // Create and configure the server with all dependencies
    // RedisServerFactory uses dependency injection to wire up all components:
    // - Core services (data store, command registry, expiration, connections)
    // - Command handlers (GET, SET, ZADD, ZRANGE, EXPIRE, etc.)
    // - Infrastructure (network server, processor, background tasks)
    var server = RedisServerFactory.CreateServer();

    // Start the main event loop
    // This will run until the cancellation token is triggered (Ctrl+C)
    // The event loop:
    // 1. Waits for network events using Socket.Select()
    // 2. Processes incoming commands
    // 3. Runs background tasks (expiration, idle cleanup)
    await server.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // This is expected when the user presses Ctrl+C
    // The cancellation token triggers this exception to exit the event loop
    Console.WriteLine("[Shutdown] Server stopped gracefully");
}
catch (Exception ex)
{
    // Unexpected error occurred
    // Log the error for debugging purposes
    Console.WriteLine($"[Fatal Error] {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}

Console.WriteLine("[Shutdown] Server shutdown complete");
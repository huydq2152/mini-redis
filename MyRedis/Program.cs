using MyRedis.Infrastructure;
using MyRedis.Infrastructure.Server;
using MyRedis.System.Tasks;

// ============================================================================
// MyRedis Server - Main Entry Point
// ============================================================================

Console.WriteLine("Starting MyRedis Server...");
Console.WriteLine("Architecture: Clean, SOLID-compliant implementation");
Console.WriteLine("Features: Command handlers, dependency injection, separation of concerns");
Console.WriteLine("Press Ctrl+C to stop the server");

// Declare backgroundTaskSystem in outer scope for access in finally block
BackgroundTaskSystem? backgroundTaskSystem = null;

try
{
    // Set up cancellation token for graceful shutdown
    // This allows us to cleanly exit the event loop when the user presses Ctrl+C
    using var cts = new CancellationTokenSource();
    var cancelled = false;

    // Register a handler for Ctrl+C (SIGINT)
    // This is the standard way to gracefully shut down a server application
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
    // RedisServerFactory uses dependency injection to wire up all components
    // Returns both the server orchestrator and background task system for shutdown control
    (var server, backgroundTaskSystem) = RedisServerFactory.CreateServer();

    // Start the main event loop
    // This will run until the cancellation token is triggered (Ctrl+C)
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
finally
{
    // Graceful shutdown: Wait for background tasks to complete
    // This ensures large object deletions finish before process exits
    // Critical for preventing "zombie tasks" and ensuring clean termination
    if (backgroundTaskSystem != null)
    {
        Console.WriteLine("[Shutdown] Waiting for background tasks to complete...");

        try
        {
            // Wait up to 10 seconds for all workers to finish their queues
            // Each worker has its own timeout (LazyFree: 5s, Persistence: 30s)
            // System timeout is global - whichever completes first
            bool cleanShutdown = await backgroundTaskSystem.ShutdownAsync(TimeSpan.FromSeconds(10));

            Console.WriteLine(cleanShutdown
                ? "[Shutdown] All background tasks completed successfully"
                // Timeout occurred - some tasks were forcibly stopped
                // Acceptable for LazyFree (just memory cleanup)
                // Warning for Persistence (potential data loss in future)
                : "[Shutdown] Warning: Some background tasks timed out (forced shutdown)");
        }
        catch (Exception ex)
        {
            // Unexpected error during shutdown
            // Log but continue shutdown (don't block process exit)
            Console.WriteLine($"[Shutdown] Error during background task shutdown: {ex.Message}");
        }
    }
}

Console.WriteLine("[Shutdown] Server shutdown complete");
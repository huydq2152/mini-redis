using MyRedis.Infrastructure;

Console.WriteLine("Starting MyRedis Server...");
Console.WriteLine("Architecture: Clean, SOLID-compliant implementation");
Console.WriteLine("Features: Command handlers, dependency injection, separation of concerns");
Console.WriteLine("Press Ctrl+C to stop the server");

try
{
    // Set up cancellation token for graceful shutdown
    using var cts = new CancellationTokenSource();
    var cancelled = false;
    
    Console.CancelKeyPress += (_, e) =>
    {
        if (!cancelled)
        {
            Console.WriteLine("\n[Shutdown] Graceful shutdown initiated...");
            e.Cancel = true;
            cancelled = true;
            cts.Cancel();
        }
    };

    var server = RedisServerFactory.CreateServer();
    await server.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("[Shutdown] Server stopped gracefully");
}
catch (Exception ex)
{
    Console.WriteLine($"[Fatal Error] {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}

Console.WriteLine("[Shutdown] Server shutdown complete");
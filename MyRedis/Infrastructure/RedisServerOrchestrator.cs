namespace MyRedis.Infrastructure;

/// <summary>
/// Main server orchestrator that coordinates all components
/// Single Responsibility: High-level server coordination and lifecycle management
/// </summary>
public class RedisServerOrchestrator
{
    private readonly NetworkServer _networkServer;
    private readonly CommandProcessor _commandProcessor;
    private readonly BackgroundTaskManager _backgroundTaskManager;

    public RedisServerOrchestrator(
        NetworkServer networkServer,
        CommandProcessor commandProcessor,
        BackgroundTaskManager backgroundTaskManager)
    {
        _networkServer = networkServer ?? throw new ArgumentNullException(nameof(networkServer));
        _commandProcessor = commandProcessor ?? throw new ArgumentNullException(nameof(commandProcessor));
        _backgroundTaskManager = backgroundTaskManager ?? throw new ArgumentNullException(nameof(backgroundTaskManager));
    }

    /// <summary>
    /// Runs the main server loop
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop the server</param>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("[Server] Starting main event loop...");

        while (!cancellationToken.IsCancellationRequested)
        {
            // Calculate timeout for select operation
            int selectTimeout = _backgroundTaskManager.GetNextTimeout();
            int selectMicroSeconds = selectTimeout * 1000;

            // Process network events
            var connectionData = _networkServer.ProcessNetworkEvents(selectMicroSeconds);

            // Process commands for connections that received data
            foreach (var (connection, _) in connectionData)
            {
                await _commandProcessor.ProcessConnectionDataAsync(connection);
            }

            // Process background tasks
            _backgroundTaskManager.ProcessBackgroundTasks();
        }
        
        Console.WriteLine("[Server] Main event loop stopped");
    }
}
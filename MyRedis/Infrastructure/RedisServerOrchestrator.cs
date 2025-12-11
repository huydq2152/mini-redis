namespace MyRedis.Infrastructure;

/// <summary>
/// Orchestrates the main event loop and coordinates all server components.
///
/// Responsibility: Server Lifecycle and Event Loop Coordination
/// - Run the main select()-based event loop
/// - Coordinate network I/O, command processing, and background tasks
/// - Ensure proper sequencing of operations
/// - Handle graceful shutdown
///
/// Architecture Overview:
/// RedisServerOrchestrator is the "conductor" that brings together:
/// 1. NetworkServer: Handles TCP connections and I/O
/// 2. CommandProcessor: Parses and executes Redis commands
/// 3. BackgroundTaskManager: Runs maintenance tasks (expiration, idle cleanup)
///
/// Event Loop Flow:
/// The event loop runs continuously until cancelled:
///
/// 1. Calculate Timeout:
///    - Ask BackgroundTaskManager when next maintenance is needed
///    - Convert to microseconds for Socket.Select()
///
/// 2. Wait for Network Events:
///    - NetworkServer.ProcessNetworkEvents() blocks in Select()
///    - Wakes up when: client sends data, new connection, or timeout
///    - Returns list of connections that received data
///
/// 3. Process Commands:
///    - For each connection with data:
///      a. CommandProcessor parses commands from buffer
///      b. Commands are executed (handlers modify data store)
///      c. Responses are sent to client
///
/// 4. Process Background Tasks:
///    - BackgroundTaskManager.ProcessBackgroundTasks()
///    - Deletes expired keys
///    - Closes idle connections
///
/// 5. Repeat:
///    - Loop back to step 1
///
/// Why This Design?
/// - Single-threaded: Simple, no locking needed
/// - Non-blocking: Never waits unnecessarily
/// - Efficient: Wakes up only when work is needed
/// - Scalable: Can handle thousands of connections
///
/// Similar to Redis Architecture:
/// This design mirrors Redis itself:
/// - Select-based event loop (not threads per connection)
/// - Single-threaded command processing (no race conditions)
/// - Background tasks run between command processing
/// - Dynamic timeout calculation (wake up when needed)
///
/// Performance Characteristics:
/// - Latency: Microseconds per command (single-threaded = no context switching)
/// - Throughput: Thousands of commands per second (pipelining support)
/// - Connections: Can handle 1000s (limited by OS, not architecture)
/// - CPU: Single core usage (additional cores don't help)
///
/// Shutdown Behavior:
/// When cancellationToken is triggered:
/// - Current event loop iteration completes
/// - No new connections accepted
/// - Existing connections can finish current commands
/// - Clean shutdown (no abrupt termination)
///
/// Design Pattern: Orchestrator Pattern
/// - Doesn't do work itself, delegates to specialists
/// - Coordinates sequencing and timing
/// - Single point of control for the entire server
/// </summary>
public class RedisServerOrchestrator
{
    // Handles network I/O (accept connections, read/write data)
    private readonly NetworkServer _networkServer;

    // Handles command parsing and execution
    private readonly CommandProcessor _commandProcessor;

    // Handles background maintenance (expiration, idle cleanup)
    private readonly BackgroundTaskManager _backgroundTaskManager;

    /// <summary>
    /// Creates the orchestrator with all required components.
    ///
    /// Dependencies are injected to maintain loose coupling and testability.
    /// All parameters are required (null check enforced).
    ///
    /// Component Responsibilities:
    /// - NetworkServer: All networking (TCP, sockets, connections)
    /// - CommandProcessor: All command handling (parse, route, execute)
    /// - BackgroundTaskManager: All maintenance (expiration, idle cleanup)
    ///
    /// Orchestrator Responsibility:
    /// - Coordinate these components in the event loop
    /// - Ensure proper sequencing
    /// - Nothing else (doesn't do work itself)
    /// </summary>
    public RedisServerOrchestrator(
        NetworkServer networkServer,
        CommandProcessor commandProcessor,
        BackgroundTaskManager backgroundTaskManager)
    {
        // Validate all dependencies (fail-fast if misconfigured)
        _networkServer = networkServer ?? throw new ArgumentNullException(nameof(networkServer));
        _commandProcessor = commandProcessor ?? throw new ArgumentNullException(nameof(commandProcessor));
        _backgroundTaskManager = backgroundTaskManager ?? throw new ArgumentNullException(nameof(backgroundTaskManager));
    }

    /// <summary>
    /// Runs the main server event loop.
    ///
    /// This is the heart of the Redis server. It runs continuously until cancelled,
    /// processing network events and background tasks.
    ///
    /// Event Loop Pattern:
    /// This implements the classic select()-based event loop:
    ///
    /// while (not cancelled):
    ///   1. timeout = calculate when next background task is needed
    ///   2. events = wait for network events or timeout (Select)
    ///   3. process all events (read data, execute commands, send responses)
    ///   4. process background tasks (expiration, idle cleanup)
    ///
    /// Step-by-Step Execution:
    ///
    /// 1. GetNextTimeout():
    ///    - When does next key expire?
    ///    - When does next connection become idle?
    ///    - Use minimum (earliest event)
    ///    - This ensures we wake up in time for maintenance
    ///
    /// 2. ProcessNetworkEvents():
    ///    - Socket.Select() waits for events
    ///    - Returns when: client sends data, timeout, or error
    ///    - Result: List of connections that have data to process
    ///
    /// 3. ProcessConnectionDataAsync():
    ///    - For each connection with data:
    ///      a. Parse commands from buffer
    ///      b. Execute commands (call handlers)
    ///      c. Send responses to client
    ///    - May process multiple commands (pipelining)
    ///
    /// 4. ProcessBackgroundTasks():
    ///    - Delete expired keys (active expiration)
    ///    - Close idle connections (cleanup)
    ///    - These run "between" commands (not during)
    ///
    /// Why Async?
    /// - Command handlers may be async (future flexibility)
    /// - Event loop can await without blocking
    /// - But currently single-threaded (no parallelism)
    ///
    /// Cancellation:
    /// - CancellationToken allows graceful shutdown
    /// - Loop exits after current iteration completes
    /// - Existing commands finish processing
    /// - No abrupt termination mid-command
    ///
    /// Performance:
    /// - Microsecond latency for simple commands
    /// - Thousands of commands per second
    /// - Zero CPU usage when idle (Select blocks)
    /// - Wakes up only when work is needed
    ///
    /// Example Timeline:
    /// - 00:00.000 - Calculate timeout (500ms until key expires)
    /// - 00:00.000 - Select waits
    /// - 00:00.100 - Client sends GET command (Select wakes up)
    /// - 00:00.100 - Process GET, send response
    /// - 00:00.100 - Check background tasks (nothing to do)
    /// - 00:00.100 - Calculate timeout (400ms until key expires)
    /// - 00:00.100 - Select waits
    /// - 00:00.500 - Timeout (key expired)
    /// - 00:00.500 - Process background tasks (delete expired key)
    /// - 00:00.500 - Loop continues...
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop the server gracefully</param>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("[Server] Starting main event loop...");

        // Main event loop: runs continuously until cancellation requested
        while (!cancellationToken.IsCancellationRequested)
        {
            // STEP 1: Calculate how long we can sleep
            // BackgroundTaskManager tells us when the next maintenance task is needed
            // Returns milliseconds until next expiration or idle check
            int selectTimeout = _backgroundTaskManager.GetNextTimeout();

            // Convert to microseconds for Socket.Select()
            // Socket.Select() uses microseconds, BackgroundTaskManager uses milliseconds
            int selectMicroSeconds = selectTimeout * 1000;

            // STEP 2: Wait for network events or timeout
            // ProcessNetworkEvents() blocks in Socket.Select() until:
            // - A client sends data (returns connection with data)
            // - Timeout expires (time to run background tasks)
            // - Error occurs (connection closed)
            var connectionData = _networkServer.ProcessNetworkEvents(selectMicroSeconds);

            // STEP 3: Process all connections that received data
            // For each connection with data in its buffer:
            // - Parse commands (may be multiple due to pipelining)
            // - Execute commands (call handlers)
            // - Send responses (flush to client)
            foreach (var (connection, _) in connectionData)
            {
                // Process all commands in this connection's buffer
                // Returns number of commands processed (for logging/metrics)
                await _commandProcessor.ProcessConnectionDataAsync(connection);

                // Check if this connection has pending writes (partial send occurred)
                // This happens when:
                // - Response is large (1MB+ from MGET, ZRANGE, etc.)
                // - Client is slow (network congestion, limited bandwidth)
                // - Kernel send buffer is full (high throughput scenario)
                //
                // Performance: Direct field check is O(1)
                if (connection.WriteBufferOffset > 0 && connection.WriteBufferOffset < connection.WrittenCount)
                {
                    // Register for write monitoring in next event loop iteration
                    // NetworkServer will include this socket in writeList for Select()
                    _networkServer.RegisterPendingWrite(connection.Socket);
                }
            }

            // STEP 4: Run background maintenance tasks
            // These run "between" processing commands:
            // - Active expiration: Delete keys whose TTL expired
            // - Idle cleanup: Close connections with no recent activity
            // Both operations are throttled to prevent long-running work
            _backgroundTaskManager.ProcessBackgroundTasks();

            // STEP 5: Loop back to step 1
            // This continues indefinitely until cancellation requested
        }

        // Shutdown: Cancellation token was triggered
        // Event loop exits gracefully after current iteration completes
        Console.WriteLine("[Server] Main event loop stopped");
    }
}
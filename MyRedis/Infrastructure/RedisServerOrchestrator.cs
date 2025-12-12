namespace MyRedis.Infrastructure;

/// <summary>
/// Orchestrates the main event loop and coordinates all server components.
///
/// Responsibility: Server Lifecycle and Event Loop Coordination
/// - Run the main select()-based event loop
/// - Coordinate network I/O, command processing, and background tasks
/// - Ensure proper sequencing of operations
/// - Handle graceful shutdown
/// - Manage fairness via resume list for connections with pending commands
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
/// 1. Process Resume List:
///    - Connections with pending buffered commands (processed 16, have more)
///    - Process these FIRST before checking network
///    - Prevents "Stalled Processing" bug (buffered data not in socket)
///
/// 2. Calculate Timeout:
///    - If resume list non-empty: timeout = 0 (non-blocking poll)
///    - Else: Ask BackgroundTaskManager when next maintenance needed
///    - Convert to microseconds for Socket.Select()
///
/// 3. Wait for Network Events:
///    - NetworkServer.ProcessNetworkEvents() blocks in Select()
///    - Wakes up when: client sends data, new connection, or timeout
///    - Returns list of connections that received data
///
/// 4. Process Commands:
///    - For each connection with data:
///      a. CommandProcessor parses up to 16 commands from buffer
///      b. Commands are executed (handlers modify data store)
///      c. Responses are sent to client
///      d. If hasMore=true, add to resume list (more buffered data)
///
/// 5. Process Background Tasks:
///    - BackgroundTaskManager.ProcessBackgroundTasks()
///    - Deletes expired keys
///    - Closes idle connections
///
/// 6. Repeat:
///    - Loop back to step 1
///
/// Fairness & Starvation Prevention:
/// The Resume List pattern prevents Head-of-Line Blocking:
/// - Each connection processes max 16 commands per iteration
/// - If more remain, connection added to _resumeList
/// - Resume list processed BEFORE network I/O
/// - This ensures round-robin fairness among all connections
///
/// Why Resume List Is Critical:
/// Without it, we'd have "Stalled Processing" bug:
/// 1. Client sends 100 commands
/// 2. Process 16, buffer still has 84 commands
/// 3. Select() waits for NEW network data
/// 4. But data is in APPLICATION buffer, not socket buffer
/// 5. Server sleeps until client sends MORE data (stalled!)
///
/// With resume list:
/// 1. Process 16, detect BytesRead > 0, return hasMore=true
/// 2. Add to _resumeList
/// 3. Next iteration: Process resume list FIRST (don't sleep)
/// 4. Process next 16, repeat until buffer empty
///
/// Why This Design?
/// - Single-threaded: Simple, no locking needed
/// - Fair: No connection can monopolize the server
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
/// - Command limiting for fairness (processInputBuffer chunks)
///
/// Performance Characteristics:
/// - Latency: Microseconds per command (single-threaded = no context switching)
/// - Latency P99: No starvation (fairness guarantees)
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
    /// Tracks connections with pending buffered commands.
    ///
    /// Problem Statement:
    /// When a connection has buffered commands but we've hit MAX_COMMANDS_PER_LOOP (16),
    /// we need to resume processing it later. But Socket.Select() only wakes on NEW
    /// network data, not buffered application data.
    ///
    /// Solution - Resume List:
    /// - CommandProcessor returns (processed, hasMore=true) when buffer has data
    /// - We add connection to this HashSet
    /// - NEXT iteration: Process resume list FIRST (before Select)
    /// - Set Select timeout=0 if resume list non-empty (non-blocking poll)
    ///
    /// Why HashSet?
    /// - O(1) add/remove/contains
    /// - Connection may be added multiple times (idempotent)
    /// - Fast iteration for small sets (typically 1-10 connections)
    ///
    /// Lifecycle:
    /// - Added: When ProcessConnectionDataAsync returns hasMore=true
    /// - Removed: When hasMore=false (buffer empty or processed < 16)
    /// - Cleared: Never (connections removed individually)
    ///
    /// Example Scenario:
    /// - Client A sends 100 pipelined commands
    /// - Iteration 1: Process 16, hasMore=true, add to _resumeList
    /// - Iteration 2: Process resume list first (A gets next 16)
    /// - ... continue until all 100 processed
    /// - Client B never waits more than 1-2 iterations (fairness!)
    ///
    /// Performance:
    /// - Memory: ~32 bytes per pending connection (minimal)
    /// - CPU: O(K) where K = pending connections (typically small)
    /// - Fairness: Guarantees progress for all connections
    /// </summary>
    private readonly HashSet<Core.Connection> _resumeList = new();

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
            // STEP 1: Process Resume List
            // These are connections with buffered commands from previous iteration
            // Must process FIRST to prevent "Stalled Processing" bug
            // (Select won't wake for buffered data, only new network data)
            if (_resumeList.Count > 0)
            {
                // Create a snapshot to avoid modification during iteration
                // (ProcessConnectionAsync may add/remove from _resumeList)
                var resumeSnapshot = _resumeList.ToList();
                _resumeList.Clear(); // Clear before processing (will re-add if still has data)

                Console.WriteLine($"[Resume List] Processing {resumeSnapshot.Count} connections with pending commands");

                foreach (var connection in resumeSnapshot)
                {
                    await ProcessConnectionAsync(connection);
                }
            }

            // STEP 2: Calculate how long we can sleep
            // If resume list is non-empty, use timeout=0 (non-blocking poll)
            // Otherwise, ask BackgroundTaskManager when next maintenance needed
            int selectTimeout;
            if (_resumeList.Count > 0)
            {
                // Connections have pending buffered commands
                // Don't sleep - poll network immediately and resume processing
                selectTimeout = 0;
            }
            else
            {
                // No pending work, safe to sleep
                // BackgroundTaskManager tells us when the next maintenance task is needed
                selectTimeout = _backgroundTaskManager.GetNextTimeout();
            }

            // Convert to microseconds for Socket.Select()
            // Socket.Select() uses microseconds, BackgroundTaskManager uses milliseconds
            int selectMicroSeconds = selectTimeout * 1000;

            // STEP 3: Wait for network events or timeout
            // ProcessNetworkEvents() blocks in Socket.Select() until:
            // - A client sends data (returns connection with data)
            // - Timeout expires (time to run background tasks OR resume list pending)
            // - Error occurs (connection closed)
            var connectionData = _networkServer.ProcessNetworkEvents(selectMicroSeconds);

            // STEP 4: Process all connections that received data from network
            // For each connection with data in its buffer:
            // - Parse up to 16 commands (fairness limit)
            // - Execute commands (call handlers)
            // - Send responses (flush to client)
            // - If hasMore=true, add to resume list for next iteration
            foreach (var (connection, _) in connectionData)
            {
                await ProcessConnectionAsync(connection);
            }

            // STEP 5: Run background maintenance tasks
            // These run "between" processing commands:
            // - Active expiration: Delete keys whose TTL expired
            // - Idle cleanup: Close connections with no recent activity
            // Both operations are throttled to prevent long-running work
            _backgroundTaskManager.ProcessBackgroundTasks();

            // STEP 6: Loop back to step 1
            // This continues indefinitely until cancellation requested
        }

        // Shutdown: Cancellation token was triggered
        // Event loop exits gracefully after current iteration completes
        Console.WriteLine("[Server] Main event loop stopped");
    }

    /// <summary>
    /// Processes commands for a single connection (helper method for DRY).
    ///
    /// This method is called from two places:
    /// 1. Resume list processing (connections with pending buffered commands)
    /// 2. Network event processing (connections with new data from network)
    ///
    /// Processing Flow:
    /// 1. Call CommandProcessor to parse and execute up to 16 commands
    /// 2. Check return value (processed, hasMore):
    ///    - hasMore=true: Connection has more buffered commands, add to _resumeList
    ///    - hasMore=false: Connection buffer empty or processed < 16, remove from _resumeList
    /// 3. Check for pending writes (partial sends):
    ///    - If partial send, register for write monitoring
    ///
    /// Fairness Guarantee:
    /// By limiting to 16 commands and using resume list, we ensure:
    /// - No connection monopolizes the server
    /// - All connections make progress
    /// - P99 latency remains low even under heavy load
    ///
    /// Stalled Processing Prevention:
    /// By adding to _resumeList when hasMore=true:
    /// - Connections with buffered data are processed next iteration
    /// - We don't sleep in Select() while work is pending
    /// - Server remains responsive even with large pipelines
    /// </summary>
    /// <param name="connection">The connection to process</param>
    private async Task ProcessConnectionAsync(Core.Connection connection)
    {
        // Process up to MAX_COMMANDS_PER_LOOP (16) commands
        // Returns (commandsProcessed, hasMorePending)
        var (processed, hasMore) = await _commandProcessor.ProcessConnectionDataAsync(connection);

        // Fairness Management: Add/remove from resume list based on hasMore
        if (hasMore)
        {
            // Connection still has buffered commands after processing 16
            // Add to resume list so we process it again next iteration
            // HashSet.Add is idempotent (safe to add multiple times)
            _resumeList.Add(connection);
        }
        else
        {
            // Connection buffer is empty or processed < 16 commands
            // Remove from resume list (if it was there)
            // HashSet.Remove is idempotent (safe even if not present)
            _resumeList.Remove(connection);
        }

        // Write Monitoring: Check for partial sends
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
}
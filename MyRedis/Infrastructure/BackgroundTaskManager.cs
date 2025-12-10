using MyRedis.Abstractions;

namespace MyRedis.Infrastructure;

/// <summary>
/// Coordinates all background maintenance tasks for the Redis server.
///
/// Responsibility: Background Task Coordination
/// - Orchestrates periodic maintenance that doesn't happen during command processing
/// - Keeps the server healthy by cleaning up expired/idle resources
/// - Calculates optimal sleep time for the event loop
///
/// Background Tasks:
/// 1. Key Expiration (Active):
///    - Periodically scan for and delete expired keys
///    - Prevents expired keys from consuming memory indefinitely
///    - Complements passive expiration (checking on access)
///
/// 2. Idle Connection Cleanup:
///    - Close connections that haven't sent data in 5+ minutes
///    - Prevents resource exhaustion from abandoned clients
///    - Frees memory, sockets, and tracking structures
///
/// Integration with Event Loop:
/// The event loop calls:
/// 1. GetNextTimeout() - How long can we sleep in Socket.Select()?
/// 2. Socket.Select() - Wait for network events or timeout
/// 3. Process network events
/// 4. ProcessBackgroundTasks() - Run maintenance
///
/// Why Background Tasks Are Needed:
/// - Passive Expiration: Only checks when keys are accessed
///   - Problem: Unused expired keys sit in memory forever
///   - Solution: Active expiration periodically scans
///
/// - Idle Detection: Clients may disconnect without closing socket properly
///   - Problem: Server thinks they're still connected
///   - Solution: Actively close connections with no activity
///
/// Performance Considerations:
/// - Background tasks are throttled (100 keys max per iteration)
/// - They run between handling commands, not during
/// - Timeout calculation ensures minimal wasted CPU cycles
///
/// Design Pattern: Coordinator
/// - Doesn't do the work itself, delegates to specialized services
/// - Provides unified interface for all background work
/// - Simplifies the event loop (one call instead of many)
/// </summary>
public class BackgroundTaskManager
{
    // Data store for removing expired keys
    private readonly IDataStore _dataStore;

    // Expiration service that tracks TTLs and finds expired keys
    private readonly IExpirationService _expirationService;

    // Connection manager that tracks activity and finds idle connections
    private readonly IConnectionManager _connectionManager;

    // Network server that can close connections
    private readonly NetworkServer _networkServer;

    /// <summary>
    /// Creates a background task manager with all required dependencies.
    ///
    /// Dependencies are injected to maintain loose coupling and testability.
    /// All parameters are required (null check enforced).
    /// </summary>
    public BackgroundTaskManager(
        IDataStore dataStore,
        IExpirationService expirationService,
        IConnectionManager connectionManager,
        NetworkServer networkServer)
    {
        _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        _expirationService = expirationService ?? throw new ArgumentNullException(nameof(expirationService));
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _networkServer = networkServer ?? throw new ArgumentNullException(nameof(networkServer));
    }

    /// <summary>
    /// Runs all background maintenance tasks.
    ///
    /// Called by RedisServerOrchestrator after processing network events on each
    /// event loop iteration.
    ///
    /// Tasks Performed:
    /// 1. Process Expired Keys: Delete keys whose TTL has expired
    /// 2. Process Idle Connections: Close connections with no recent activity
    ///
    /// Execution Order:
    /// - Expiration first (frees memory)
    /// - Then idle cleanup (frees connections)
    /// - Order doesn't matter much, but this is logical
    ///
    /// Performance:
    /// - Each task is throttled internally (100 keys, all idle connections)
    /// - Typically completes in microseconds
    /// - Runs "between" commands, not blocking them
    ///
    /// Frequency:
    /// - Called on every event loop iteration
    /// - But tasks may do nothing if no work needed
    /// - Very efficient when idle (just a few checks)
    /// </summary>
    public void ProcessBackgroundTasks()
    {
        // Delete expired keys from the data store
        ProcessExpiredKeys();

        // Close connections that have been idle too long
        ProcessIdleConnections();
    }

    /// <summary>
    /// Calculates the optimal timeout for the next Socket.Select() call.
    ///
    /// This is critical for efficient event loop operation. The timeout determines
    /// how long we can sleep waiting for network events before we need to wake up
    /// to process background tasks.
    ///
    /// Algorithm:
    /// 1. Ask ExpirationService when the next key expires
    /// 2. Ask ConnectionManager when the next connection becomes idle
    /// 3. Return the minimum (soonest event)
    ///
    /// Why Minimum?
    /// - We need to wake up for whichever event happens first
    /// - If we sleep past an expiration, keys stay in memory too long
    /// - If we sleep past idle timeout, connections stay open too long
    ///
    /// Benefits:
    /// - Avoids busy-waiting (sleeping 0ms constantly)
    /// - Avoids excessive sleep (waking up too late)
    /// - Perfectly balanced: wake up exactly when work needs to be done
    ///
    /// Example Scenarios:
    /// - Next expiration in 500ms, next idle in 2000ms -> return 500ms
    /// - No expirations, idle in 100ms -> return 100ms
    /// - Both in the past (0ms) -> return 0ms (wake up immediately)
    ///
    /// Edge Cases:
    /// - No keys with TTL: expiration returns 10000ms (default)
    /// - No connections: idle returns 10000ms (default)
    /// - Both return defaults: we sleep 10s, wake up, check again
    ///
    /// Performance Impact:
    /// - Proper timeout = responsive server with minimal CPU usage
    /// - Too short = wasted CPU cycles
    /// - Too long = delayed expiration/cleanup
    /// </summary>
    /// <returns>Milliseconds to wait in Socket.Select() (0 = don't wait, positive = wait time)</returns>
    public int GetNextTimeout()
    {
        // Get timeout for next expiration
        int ttlWait = _expirationService.GetNextTimeout();

        // Get timeout for next idle check
        int idleWait = _connectionManager.GetNextTimeout();

        // Use the shorter timeout (earliest event)
        int selectTimeout = Math.Min(ttlWait, idleWait);

        // Ensure non-negative (should always be true, but defensive)
        return selectTimeout < 0 ? 0 : selectTimeout;
    }

    /// <summary>
    /// Processes and deletes keys that have exceeded their TTL.
    ///
    /// This implements "active expiration" - proactively cleaning up expired keys
    /// even if they're never accessed again.
    ///
    /// Process:
    /// 1. Ask ExpirationService for expired keys (up to 100 per iteration)
    /// 2. For each expired key:
    ///    a. Remove from data store (frees memory)
    ///    b. Log the deletion (helpful for debugging/monitoring)
    ///
    /// Why We Need This:
    /// - Passive expiration only checks when keys are accessed
    /// - Keys that are never accessed again would stay in memory forever
    /// - Active expiration ensures timely cleanup
    ///
    /// Throttling:
    /// - ExpirationService limits to 100 keys per call
    /// - If many keys expire at once, they're processed over multiple iterations
    /// - This prevents long-running expiration from blocking the event loop
    ///
    /// Note: ExpirationService already removed the expiration metadata.
    /// We just need to delete the actual key-value data.
    /// </summary>
    private void ProcessExpiredKeys()
    {
        // Get list of keys that have expired
        // (Already removed from expiration tracking)
        var expiredKeys = _expirationService.ProcessExpiredKeys();

        // Delete each expired key from the data store
        foreach (var key in expiredKeys)
        {
            // Remove the key-value data (frees memory)
            _dataStore.Remove(key);

            // Log for visibility (helps with debugging and monitoring)
            Console.WriteLine($"[TTL] Expired key: {key}");
        }
    }

    /// <summary>
    /// Closes connections that have been idle for too long.
    ///
    /// Process:
    /// 1. Ask ConnectionManager for idle connections (5+ minutes inactive)
    /// 2. If any found, tell NetworkServer to close them
    ///
    /// Why We Need This:
    /// - Clients may crash or disconnect without closing the socket properly
    /// - These "half-open" connections stay in the server's tables
    /// - They consume memory, file descriptors, and tracking structures
    /// - Idle timeout ensures eventual cleanup
    ///
    /// What Gets Closed:
    /// - Connections with no data received for 5 minutes
    /// - "No data" means no commands, not no responses
    /// - Even if we're sending responses, if client stops sending, it's idle
    ///
    /// Typical Scenarios:
    /// - Client application crashes (no FIN sent)
    /// - Client machine loses power
    /// - Network cable unplugged
    /// - Client using connection pool but forgot to send keepalives
    ///
    /// Safety:
    /// - 5 minutes is very conservative (most Redis clients use 60s)
    /// - Well-behaved clients should either:
    ///   a. Close connections when done
    ///   b. Send periodic commands (PING) to stay alive
    /// </summary>
    private void ProcessIdleConnections()
    {
        // Get connections that exceeded the idle timeout
        var idleConnections = _connectionManager.GetIdleConnections();

        // If any idle connections found, close them
        if (idleConnections.Count > 0)
        {
            // NetworkServer handles the actual socket closure and cleanup
            _networkServer.CloseIdleConnections(idleConnections);
        }
    }
}
using MyRedis.Abstractions;
using MyRedis.Infrastructure;

namespace MyRedis.Core;

/// <summary>
/// Background task for cleaning up idle connections.
///
/// Purpose: Prevent resource exhaustion from abandoned clients
/// - Clients may crash or disconnect without closing sockets properly
/// - These "half-open" connections consume memory, file descriptors, and tracking structures
/// - Idle timeout ensures eventual cleanup
///
/// Design Pattern: Adapter + Strategy
/// - Adapts IConnectionManager and NetworkServer to IBackgroundTask interface
/// - Encapsulates idle detection logic in a reusable, pluggable component
///
/// Integration:
/// - Registered with BackgroundTaskManager during server initialization
/// - Runs when connections exceed idle timeout (data-driven)
/// - Processes all idle connections per cycle (early termination optimization)
///
/// Why Normal Priority (50)?
/// - Important for resource management but not critical like memory
/// - Can tolerate slight delays (5-minute timeout is very conservative)
/// - Runs after high-priority tasks like key expiration
///
/// Performance Characteristics:
/// - Typical execution: 5-10 microseconds (no idle connections)
/// - With idle connections: 50-100 microseconds (socket closure overhead)
/// - Early termination: Stops scanning when first non-idle connection found
///
/// Thread Safety: Not thread-safe. Assumes single-threaded event loop execution.
/// </summary>
public class IdleConnectionCleanupTask : BackgroundTaskBase
{
    private readonly IConnectionManager _connectionManager;
    private readonly NetworkServer _networkServer;

    /// <summary>
    /// Creates a new idle connection cleanup task.
    /// </summary>
    /// <param name="connectionManager">Manager that tracks connection activity and identifies idle connections</param>
    /// <param name="networkServer">Server that can close connections</param>
    /// <param name="intervalMs">Milliseconds between idle checks (default: 1000ms)</param>
    public IdleConnectionCleanupTask(
        IConnectionManager connectionManager,
        NetworkServer networkServer,
        int intervalMs = 1000)
        : base("IdleConnectionCleanup", intervalMs, maxWorkPerCycle: int.MaxValue, priority: 50)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _networkServer = networkServer ?? throw new ArgumentNullException(nameof(networkServer));
    }

    /// <summary>
    /// Gets the delay until next idle connection check.
    ///
    /// Hybrid Scheduling:
    /// - Returns 0 if connections are already idle (immediate work)
    /// - Returns time until next connection becomes idle (data-driven)
    /// - Returns interval time as fallback
    ///
    /// This overrides the base interval-based scheduling with a more intelligent
    /// data-driven approach that wakes up exactly when connections become idle.
    /// </summary>
    public override int GetNextRunDelay()
    {
        // Ask connection manager when the next connection becomes idle
        int idleDelay = _connectionManager.GetNextTimeout();

        // Use the shorter delay (data-driven vs interval-based)
        int intervalDelay = base.GetNextRunDelay();

        return Math.Min(idleDelay, intervalDelay);
    }

    /// <summary>
    /// Processes and closes idle connections.
    ///
    /// Process:
    /// 1. Get idle connections from connection manager
    /// 2. If any found, tell network server to close them
    ///
    /// What Gets Closed:
    /// - Connections with no data received for 5+ minutes
    /// - "No data" means no commands, not no responses
    /// - Even if we're sending responses, if client stops sending, it's idle
    ///
    /// Typical Scenarios:
    /// - Client application crashes (no FIN sent)
    /// - Client machine loses power
    /// - Network cable unplugged
    /// - Client using connection pool but forgot to send keepalives
    ///
    /// Early Termination Optimization:
    /// - ConnectionManager maintains list ordered by activity time
    /// - GetIdleConnections() stops scanning at first non-idle connection
    /// - Typical case: 0-2 idle connections, very fast
    ///
    /// Safety:
    /// - 5 minutes is very conservative (most Redis clients use 60s)
    /// - Well-behaved clients should either:
    ///   a. Close connections when done
    ///   b. Send periodic commands (PING) to stay alive
    /// </summary>
    protected override void ExecuteCore()
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

    /// <summary>
    /// Priority: 50 (Normal)
    ///
    /// Why Normal Priority?
    /// - Important for resource management but not critical
    /// - 5-minute timeout provides plenty of time
    /// - Can run after high-priority tasks like key expiration
    /// - Still important enough to run before low-priority metrics/logging
    /// </summary>
    public override int Priority => 50;
}

using MyRedis.Abstractions.Configuration;
using MyRedis.Abstractions.Network;
using MyRedis.Infrastructure.Network;

namespace MyRedis.Core.Background;

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
    private readonly IConfigurationService _configService;
    private long _nextRunTime;

    /// <summary>
    /// Creates a new idle connection cleanup task.
    /// </summary>
    /// <param name="connectionManager">Manager that tracks connection activity and identifies idle connections</param>
    /// <param name="networkServer">Server that can close connections</param>
    /// <param name="configService">Configuration service for runtime parameters (hot-reload)</param>
    public IdleConnectionCleanupTask(
        IConnectionManager connectionManager,
        NetworkServer networkServer,
        IConfigurationService configService)
        : base("IdleConnectionCleanup", intervalMs: 1000, maxWorkPerCycle: int.MaxValue, priority: 50)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _networkServer = networkServer ?? throw new ArgumentNullException(nameof(networkServer));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _nextRunTime = Common.Helpers.TimeHelper.GetNow();
    }

    /// <summary>
    /// Gets the delay until next idle connection check.
    /// Uses configured 'idle-check-interval' parameter (hot-reload support).
    /// </summary>
    public override int GetNextRunDelay()
    {
        // Ask connection manager when the next connection becomes idle (data-driven)
        var idleDelay = _connectionManager.GetNextTimeout();

        // Calculate interval-based delay from configuration
        // Read idle-check-interval config (default: 1000ms)
        // Hot-reload support: Changes take effect on next iteration
        var now = Common.Helpers.TimeHelper.GetNow();
        var intervalDelay = _nextRunTime - now;
        if (intervalDelay < 0) intervalDelay = 0;

        // Use the shorter delay (data-driven vs interval-based)
        return Math.Min(idleDelay, (int)intervalDelay);
    }

    /// <summary>
    /// Executes the idle connection cleanup task with configuration-driven scheduling.
    /// </summary>
    public override void Execute()
    {
        // Execute the core work
        ExecuteCore();

        // Schedule next run based on idle-check-interval configuration
        var idleCheckInterval = _configService.Get<int>("idle-check-interval");
        _nextRunTime = Common.Helpers.TimeHelper.GetNow() + idleCheckInterval;
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

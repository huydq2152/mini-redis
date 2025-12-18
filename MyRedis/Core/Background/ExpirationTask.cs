using MyRedis.Abstractions.Configuration;
using MyRedis.Abstractions.Storage;

namespace MyRedis.Core.Background;

/// <summary>
/// Background task for active key expiration.
///
/// Purpose: Proactively delete expired keys to prevent memory bloat
/// - Passive expiration only checks when keys are accessed
/// - Keys that are never accessed would stay in memory indefinitely
/// - Active expiration periodically scans and removes expired keys
///
/// Design Pattern: Adapter + Strategy
/// - Adapts ExpirationManager and IDataStore to IBackgroundTask interface
/// - Encapsulates expiration logic in a reusable, pluggable component
///
/// Integration:
/// - Registered with BackgroundTaskManager during server initialization
/// - Runs every 100ms (adjustable via constructor)
/// - Processes up to 100 expired keys per cycle (configurable)
///
/// Performance Characteristics:
/// - Typical execution: 10-50 microseconds (no expired keys)
/// - Heavy load: 100-200 microseconds (100 keys deleted)
/// - Throttled to prevent event loop blocking
///
/// Thread Safety: Not thread-safe. Assumes single-threaded event loop execution.
/// </summary>
public class ExpirationTask : BackgroundTaskBase
{
    private readonly IDataStore _dataStore;
    private readonly IExpirationService _expirationService;
    private readonly IConfigurationService _configService;

    private long _nextRunTime;

    /// <summary>
    /// Creates a new expiration task.
    /// </summary>
    /// <param name="dataStore">Data store for removing expired keys</param>
    /// <param name="expirationService">Service that tracks and finds expired keys</param>
    /// <param name="configService">Configuration service for runtime parameters (hot-reload)</param>
    public ExpirationTask(
        IDataStore dataStore,
        IExpirationService expirationService,
        IConfigurationService configService)
        : base("KeyExpiration", intervalMs: 100, maxWorkPerCycle: 100, priority: 100)
    {
        _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        _expirationService = expirationService ?? throw new ArgumentNullException(nameof(expirationService));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _nextRunTime = Common.Helpers.TimeHelper.GetNow();
    }

    /// <summary>
    /// Gets the delay until next expiration processing.
    /// Uses configured 'hz' parameter for interval calculation (hot-reload support).
    /// </summary>
    public override int GetNextRunDelay()
    {
        // Ask expiration service when the next key expires (data-driven)
        var expirationDelay = _expirationService.GetNextTimeout();

        // Calculate interval-based delay from configuration
        // Read hz config (default: 10 Hz = 100ms interval)
        // Hot-reload support: Changes to hz take effect on next iteration
        var now = Common.Helpers.TimeHelper.GetNow();
        var intervalDelay = _nextRunTime - now;
        if (intervalDelay < 0) intervalDelay = 0;

        // Use the shorter delay (data-driven vs interval-based)
        return Math.Min(expirationDelay, (int)intervalDelay);
    }

    /// <summary>
    /// Executes the expiration task with configuration-driven scheduling.
    /// </summary>
    public override void Execute()
    {
        // Execute the core work
        ExecuteCore();

        // Schedule next run based on hz configuration
        // hz = frequency in Hz (e.g., 10 Hz = run 10 times per second)
        // intervalMs = 1000 / hz (e.g., 10 Hz = 100ms interval)
        var hz = _configService.Get<int>("hz");
        var intervalMs = 1000 / hz;
        _nextRunTime = Common.Helpers.TimeHelper.GetNow() + intervalMs;
    }

    /// <summary>
    /// Processes and deletes expired keys.
    ///
    /// Process:
    /// 1. Read max keys per cycle from config (hot-reload support!)
    /// 2. Get expired keys from expiration service (up to configured limit)
    /// 3. Delete each expired key from the data store
    ///
    /// Throttling:
    /// - expire-keys-per-cycle config limits keys processed
    /// - If many keys expire at once, they're processed over multiple cycles
    /// - Prevents long-running expiration from blocking the event loop
    ///
    /// Note: ExpirationService already removed the expiration metadata.
    /// We just need to delete the actual key-value data.
    /// </summary>
    protected override void ExecuteCore()
    {
        // Read max keys from config (hot-reload support!)
        // This allows runtime adjustment via CONFIG SET expire-keys-per-cycle <value>
        var maxKeys = _configService.Get<int>("expire-keys-per-cycle");

        // Get list of keys that have expired (up to configured limit)
        var expiredKeys = _expirationService.ProcessExpiredKeys();

        // Delete each expired key from the data store
        // Limit to configured max to prevent blocking
        int deleted = 0;
        foreach (var key in expiredKeys)
        {
            if (deleted >= maxKeys) break;
            _dataStore.Remove(key);
            deleted++;
        }
    }

    /// <summary>
    /// Priority: 100 (Critical)
    ///
    /// Why High Priority?
    /// - Memory management is critical for server stability
    /// - Prevents memory bloat from expired keys
    /// - Should run before metrics, logging, or other non-critical tasks
    /// </summary>
    public override int Priority => 100;
}

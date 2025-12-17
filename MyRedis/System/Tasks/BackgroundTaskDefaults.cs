using MyRedis.System.Workers;

namespace MyRedis.System.Tasks;

/// <summary>
/// Centralized default configurations for background task categories.
/// </summary>
public static class BackgroundTaskDefaults
{
    /// <summary>
    /// Default options for LazyFree category (async deletion of large objects).
    /// </summary>
    private static CategoryWorker.Options LazyFree => new()
    {
        MaxQueueSize = 0,        // Unbounded - accept all free requests
        ShutdownTimeoutMs = 5000 // 5s - allow time for large objects
    };

    /// <summary>
    /// Default options for Persistence category (AOF fsync, RDB save).
    /// </summary>
    private static CategoryWorker.Options Persistence => new()
    {
        MaxQueueSize = 100,       // Bounded - backpressure if disk too slow
        ShutdownTimeoutMs = 30000 // 30s - must complete for durability
    };

    /// <summary>
    /// Default options for FileOps category (general file operations).
    /// </summary>
    private static CategoryWorker.Options FileOps => new()
    {
        MaxQueueSize = 1000,
        ShutdownTimeoutMs = 5000
    };

    /// <summary>
    /// Default options for Maintenance category (low-priority housekeeping).
    /// </summary>
    private static CategoryWorker.Options Maintenance => new()
    {
        MaxQueueSize = 100,
        ShutdownTimeoutMs = 2000 // Low priority, can abort
    };

    /// <summary>
    /// Gets minimal configuration for MyRedis server (YAGNI principle).
    /// Current usage:
    /// - LazyFree: DEL command async deletion
    /// - Persistence: Future AOF/RDB implementation
    /// </summary>
    public static Dictionary<BackgroundTaskCategory, CategoryWorker.Options> GetBackgroundTaskCategories()
    {
        return new Dictionary<BackgroundTaskCategory, CategoryWorker.Options>
        {
            [BackgroundTaskCategory.LazyFree] = LazyFree,
            [BackgroundTaskCategory.Persistence] = Persistence
        };
    }
}

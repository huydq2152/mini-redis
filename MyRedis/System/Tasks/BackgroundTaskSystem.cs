using MyRedis.System.Workers;

namespace MyRedis.System.Tasks;

/// <summary>
/// Background task system
///
/// Design Principles (addressing all identified concerns):
/// 1. Category Isolation: Each task type has dedicated thread (no head-of-line blocking)
/// 2. Lifecycle Management: Proper startup, health monitoring, and graceful shutdown
/// 3. Resource Classification: CPU-bound vs I/O-bound tasks properly separated
/// 4. Observability: Health checks, metrics, and failure detection
///
/// Redis BIO Architecture Reference:
/// - BIO_CLOSE_FILE (0): Close file descriptors - isolated from others
/// - BIO_AOF_FSYNC (1): fsync() AOF - critical for durability
/// - BIO_LAZY_FREE (2): Free large objects - CPU intensive
///
/// Each category gets dedicated resources to prevent cross-contamination.
/// 
/// Coordinates multiple category workers, providing unified interface
/// for background task submission and system-wide health monitoring.
///
/// Thread-Safe: Yes (all public methods)
/// IDisposable: Yes (graceful shutdown)
/// </summary>
public sealed class BackgroundTaskSystem : IDisposable
{
    private readonly Dictionary<BackgroundTaskCategory, CategoryWorker> _workers;
    private volatile bool _disposed;

    public BackgroundTaskSystem()
    {
        var options = BackgroundTaskDefaults.GetBackgroundTaskCategories();
        _workers = new Dictionary<BackgroundTaskCategory, CategoryWorker>();

        // Initialize workers for each configured category
        foreach (var category in options.Keys)
        {
            _workers[category] = new CategoryWorker(category, options[category]);
        }
    }

    /// <summary>
    /// Submits a task to the appropriate category worker.
    /// </summary>
    /// <returns>True if submitted, false if queue full or system stopped</returns>
    public bool Submit(BackgroundTaskCategory category, Action task)
    {
        if (_disposed)
            return false;

        if (!_workers.TryGetValue(category, out var worker))
        {
            Console.WriteLine($"[BGTaskSystem] Unknown category: {category}");
            return false;
        }

        return worker.TrySubmit(task);
    }

    /// <summary>
    /// Submits with async wait if queue is full (bounded channels).
    /// </summary>
    public ValueTask<bool> SubmitAsync(BackgroundTaskCategory category, Action task,
        CancellationToken cancellation = default)
    {
        if (_disposed)
            return ValueTask.FromResult(false);

        if (!_workers.TryGetValue(category, out var worker))
            return ValueTask.FromResult(false);

        return worker.SubmitAsync(task, cancellation);
    }

    /// <summary>
    /// Gets health status of all workers.
    /// </summary>
    public Dictionary<BackgroundTaskCategory, WorkerStatus> GetSystemHealth()
    {
        var health = new Dictionary<BackgroundTaskCategory, WorkerStatus>();

        foreach (var (category, worker) in _workers)
        {
            health[category] = worker.GetStatus();
        }

        return health;
    }

    /// <summary>
    /// Checks if all workers are healthy.
    /// </summary>
    public bool IsHealthy()
    {
        return _workers.Values.All(w => w.GetStatus().IsAlive);
    }

    /// <summary>
    /// Gracefully shuts down all workers, waiting for tasks to complete.
    /// </summary>
    /// <param name="timeout">Maximum time to wait</param>
    /// <returns>True if all workers shut down cleanly</returns>
    public async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        if (_disposed)
            return true;

        _disposed = true;

        // Shutdown all workers in parallel
        var shutdownTasks = _workers.Values
            .Select(w => w.ShutdownAsync(timeout))
            .ToArray();

        var results = await Task.WhenAll(shutdownTasks);

        return results.All(r => r);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var worker in _workers.Values)
        {
            worker.Dispose();
        }
    }
}
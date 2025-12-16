namespace MyRedis.Abstractions.Background;

/// <summary>
/// Represents a background maintenance task that runs periodically in the event loop.
///
/// Design Pattern: Strategy + Template Method
/// - Each task encapsulates its own scheduling logic and execution behavior
/// - BackgroundTaskManager coordinates all tasks without knowing their specifics
/// - New tasks can be added without modifying BackgroundTaskManager (Open/Closed Principle)
///
/// Why This Interface?
/// - Enables extensibility: Add metrics, persistence, clustering without changing core code
/// - Decouples scheduling from execution: Each task controls its own timing
/// - Supports time budgeting: Tasks report max work per cycle to prevent blocking
/// - Maintains event loop efficiency: Proper timeout calculation ensures optimal sleep times
///
/// Lifecycle:
/// 1. Task is registered with BackgroundTaskManager during server initialization
/// 2. GetNextRunDelay() is called to determine when task should run next
/// 3. When delay reaches 0, Execute() is called
/// 4. Task performs bounded work (respecting MaxWorkPerCycle)
/// 5. Cycle repeats
///
/// Thread Safety: Implementations should assume single-threaded event loop execution.
/// No locks required unless you move to multi-threaded background processing.
/// </summary>
public interface IBackgroundTask
{
    /// <summary>
    /// Gets the human-readable name of this background task.
    /// Used for logging, debugging, and monitoring.
    ///
    /// Examples: "KeyExpiration", "IdleConnectionCleanup", "MetricsCollection"
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the milliseconds until this task should run next.
    ///
    /// Called by BackgroundTaskManager on each event loop iteration to calculate
    /// the optimal Socket.Select() timeout.
    ///
    /// Return values:
    /// - 0: Task is due to run immediately (has pending work)
    /// - Positive: Milliseconds until task needs to run
    /// - Large value (e.g., 10000): No pending work, can sleep for a long time
    ///
    /// Algorithm Guidelines:
    /// - Check your internal state (next scheduled time, pending work queue, etc.)
    /// - Return the time until your next scheduled execution
    /// - Return 0 if work is already overdue
    ///
    /// Performance: Should be O(1) - just a time calculation or queue peek
    ///
    /// Example Implementation:
    /// <code>
    /// public int GetNextRunDelay()
    /// {
    ///     if (_workQueue.Count > 0) return 0;  // Work pending
    ///     long now = Environment.TickCount64;
    ///     long delay = _nextRunTime - now;
    ///     return delay > 0 ? (int)delay : 0;
    /// }
    /// </code>
    /// </summary>
    /// <returns>Milliseconds until task should execute (0 = now, positive = future)</returns>
    int GetNextRunDelay();

    /// <summary>
    /// Executes a bounded amount of background work.
    ///
    /// Called by BackgroundTaskManager when GetNextRunDelay() returns 0 or negative.
    ///
    /// Time Budgeting Contract:
    /// - This method MUST complete quickly to avoid blocking the event loop
    /// - Recommended execution time: < 1ms (1000 microseconds)
    /// - Use MaxWorkPerCycle to limit how much work you do per call
    ///
    /// Incremental Work Pattern:
    /// - If you have a lot of work to do, break it into small chunks
    /// - Process a bounded amount per call (e.g., 100 keys, 10 connections)
    /// - Return and let the event loop continue
    /// - You'll be called again on the next iteration to continue
    ///
    /// State Management:
    /// - Update your internal state (next run time, work queue, etc.)
    /// - Prepare for the next invocation
    /// - Ensure idempotency if possible
    ///
    /// Example Implementation:
    /// <code>
    /// public void Execute()
    /// {
    ///     int processed = 0;
    ///     while (_workQueue.Count > 0 && processed < MaxWorkPerCycle)
    ///     {
    ///         var item = _workQueue.Dequeue();
    ///         ProcessItem(item);
    ///         processed++;
    ///     }
    ///
    ///     // Schedule next run
    ///     _nextRunTime = Environment.TickCount64 + IntervalMs;
    /// }
    /// </code>
    /// </summary>
    void Execute();

    /// <summary>
    /// Gets the maximum work units this task processes per Execute() call.
    ///
    /// Used for:
    /// - Self-documentation: Shows task's throttling behavior
    /// - Monitoring: Track if tasks are respecting time budgets
    /// - Tuning: Adjust based on performance testing
    ///
    /// What is a "work unit"?
    /// - Depends on the task implementation
    /// - For expiration: number of keys checked
    /// - For idle cleanup: number of connections checked
    /// - For metrics: number of data points collected
    ///
    /// Guidelines:
    /// - Start with 100 as a reasonable default
    /// - Adjust based on profiling (aim for &lt; 1ms execution time)
    /// - Consider the cost per unit (complex operations = lower limit)
    ///
    /// Example values:
    /// - KeyExpiration: 100 keys (fast dictionary lookups)
    /// - IdleConnectionCleanup: all connections (usually &lt; 100, early termination)
    /// - DatabaseSnapshot: 1000 keys (depends on serialization cost)
    /// </summary>
    int MaxWorkPerCycle { get; }

    /// <summary>
    /// Gets the priority of this task relative to other background tasks.
    ///
    /// When multiple tasks are due to run at the same time, higher priority
    /// tasks execute first.
    ///
    /// Priority Levels (Suggested):
    /// - 100: Critical (key expiration, memory management)
    /// - 50: Normal (idle cleanup, metrics collection)
    /// - 0: Low priority (non-urgent maintenance)
    ///
    /// Why Priority Matters:
    /// - Ensures critical tasks (like memory management) run before metrics
    /// - Provides ordering when time budget is limited
    /// - Allows graceful degradation under load (low priority tasks can be skipped)
    ///
    /// Default: 50 (normal priority)
    /// </summary>
    int Priority { get; }
}

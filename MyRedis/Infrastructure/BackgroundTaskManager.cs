using MyRedis.Abstractions;

namespace MyRedis.Infrastructure;

/// <summary>
/// Coordinates all background maintenance tasks for the Redis server using a registry pattern.
///
/// Responsibility: Background Task Coordination
/// - Orchestrates periodic maintenance that doesn't happen during command processing
/// - Keeps the server healthy by cleaning up expired/idle resources
/// - Calculates optimal sleep time for the event loop
/// - Executes tasks in priority order with optional time budgeting
///
/// Design Pattern: Registry + Strategy
/// - Registry: Tasks register themselves via Register()
/// - Strategy: Each task implements IBackgroundTask interface
/// - Coordinator: Orchestrates execution without knowing task specifics
///
/// Benefits Over Previous Implementation:
/// 1. Open/Closed Principle: Add new tasks without modifying this class
/// 2. Single Responsibility: Only coordinates tasks, doesn't implement them
/// 3. Dependency Inversion: Depends on IBackgroundTask abstraction
/// 4. Testability: Easy to mock and test individual tasks
/// 5. Extensibility: New tasks (metrics, persistence, clustering) just register
///
/// Integration with Event Loop:
/// The event loop calls:
/// 1. GetNextTimeout() - How long can we sleep in Socket.Select()?
/// 2. Socket.Select() - Wait for network events or timeout
/// 3. Process network events
/// 4. ProcessBackgroundTasks() - Run maintenance
///
/// Task Execution Order:
/// - Tasks are executed in priority order (highest first)
/// - When multiple tasks are due, higher priority tasks run first
/// - Example: KeyExpiration (100) runs before IdleConnectionCleanup (50)
///
/// Time Budgeting (Optional):
/// - If enabled, limits total background work per iteration
/// - Prevents background tasks from blocking the event loop
/// - Lower priority tasks may be skipped if budget is exhausted
/// - Default: disabled (execute all due tasks)
///
/// Performance Considerations:
/// - GetNextTimeout() is O(N) where N = number of registered tasks (typically 2-10)
/// - ProcessBackgroundTasks() is O(K log K) where K = number of due tasks
/// - Each task is throttled internally (e.g., 100 keys max per iteration)
/// - Minimal overhead when no tasks are due (~1 microsecond)
///
/// Thread Safety: Not thread-safe. Assumes single-threaded event loop execution.
///
/// Example Usage:
/// <code>
/// var manager = new BackgroundTaskManager();
/// manager.Register(new ExpirationTask(dataStore, expirationService));
/// manager.Register(new IdleConnectionCleanupTask(connectionManager, networkServer));
/// manager.Register(new MetricsCollectionTask(metricsService));
///
/// // In event loop:
/// int timeout = manager.GetNextTimeout();
/// Socket.Select(readList, null, null, timeout * 1000);
/// manager.ProcessBackgroundTasks();
/// </code>
/// </summary>
public class BackgroundTaskManager
{
    /// <summary>
    /// Registry of all background tasks.
    /// Tasks are stored in registration order, but executed in priority order.
    /// </summary>
    private readonly List<IBackgroundTask> _tasks = new List<IBackgroundTask>();

    /// <summary>
    /// Maximum time budget for background work per event loop iteration (microseconds).
    /// Default: 0 (disabled - execute all due tasks regardless of time)
    ///
    /// Recommended values:
    /// - 0: Disabled (default) - execute all due tasks
    /// - 1000: 1ms budget - good balance for most workloads
    /// - 500: 0.5ms budget - very strict, for latency-sensitive applications
    /// - 2000: 2ms budget - more relaxed, for high-throughput scenarios
    ///
    /// When to enable:
    /// - High connection count with frequent background work
    /// - Strict latency requirements (e.g., &lt; 1ms P99)
    /// - You observe background tasks blocking command processing
    ///
    /// Note: If budget is exceeded, lower priority tasks may be skipped.
    /// Critical tasks (high priority) should always complete within budget.
    /// </summary>
    public long TimeBudgetMicros { get; set; } = 0;

    /// <summary>
    /// Registers a background task with the manager.
    ///
    /// Tasks can be registered in any order - execution order is determined by priority,
    /// not registration order.
    ///
    /// This method enables the Open/Closed Principle:
    /// - Open for extension: New tasks can be registered without modifying this class
    /// - Closed for modification: No need to change BackgroundTaskManager code
    ///
    /// Example:
    /// <code>
    /// manager.Register(new ExpirationTask(dataStore, expirationService));
    /// manager.Register(new MetricsCollectionTask(metricsService));
    /// </code>
    ///
    /// Performance: O(1) - just adds to list
    /// </summary>
    /// <param name="task">The background task to register</param>
    public void Register(IBackgroundTask task)
    {
        if (task == null)
            throw new ArgumentNullException(nameof(task));

        _tasks.Add(task);
    }

    /// <summary>
    /// Calculates the optimal timeout for the next Socket.Select() call.
    ///
    /// This is critical for efficient event loop operation. The timeout determines
    /// how long we can sleep waiting for network events before we need to wake up
    /// to process background tasks.
    ///
    /// Algorithm:
    /// 1. Ask each registered task when it needs to run next
    /// 2. Return the minimum (soonest event)
    ///
    /// Why Minimum?
    /// - We need to wake up for whichever task is due first
    /// - If we sleep past a task's deadline, work is delayed
    /// - Taking the minimum ensures we never sleep too long
    ///
    /// Benefits:
    /// - Avoids busy-waiting (sleeping 0ms constantly)
    /// - Avoids excessive sleep (waking up too late)
    /// - Perfectly balanced: wake up exactly when work needs to be done
    ///
    /// Example Scenarios:
    /// - Task A due in 500ms, Task B due in 2000ms → return 500ms
    /// - Task A due in 0ms (overdue), Task B due in 1000ms → return 0ms
    /// - No tasks registered → return 10000ms (10 seconds default)
    ///
    /// Edge Cases:
    /// - No tasks registered: returns 10000ms (prevents infinite sleep)
    /// - All tasks return large values: returns the minimum large value
    /// - Multiple tasks due immediately: returns 0ms
    ///
    /// Performance: O(N) where N = number of registered tasks
    /// Typical case: N = 2-10, so ~100 nanoseconds
    /// </summary>
    /// <returns>Milliseconds to wait in Socket.Select() (0 = don't wait, positive = wait time)</returns>
    public int GetNextTimeout()
    {
        // Default timeout when no tasks are registered
        if (_tasks.Count == 0)
            return 10000; // 10 seconds

        int minTimeout = int.MaxValue;

        // Ask each task when it needs to run next
        foreach (var task in _tasks)
        {
            int taskTimeout = task.GetNextRunDelay();
            if (taskTimeout < minTimeout)
                minTimeout = taskTimeout;
        }

        // Ensure non-negative (should always be true, but defensive)
        return minTimeout < 0 ? 0 : minTimeout;
    }

    /// <summary>
    /// Processes all background tasks that are due to run.
    ///
    /// Execution Strategy:
    /// 1. Find all tasks that are due (GetNextRunDelay() returns 0)
    /// 2. Sort by priority (highest first)
    /// 3. Execute each task in priority order
    /// 4. Stop if time budget is exceeded (if enabled)
    ///
    /// Priority Ordering:
    /// - Ensures critical tasks (memory management) run before metrics
    /// - Provides predictable execution order
    /// - Allows graceful degradation under load (low priority tasks skipped)
    ///
    /// Time Budgeting:
    /// - If TimeBudgetMicros > 0, tracks elapsed time
    /// - Stops executing tasks when budget is exceeded
    /// - Lower priority tasks may be skipped
    /// - High priority tasks should complete within budget
    ///
    /// Performance:
    /// - O(K log K) where K = number of due tasks
    /// - Typical case: K = 0-2 tasks due per iteration
    /// - When K = 0: ~1 microsecond (just checks)
    /// - When K > 0: depends on task execution time (typically &lt; 100μs total)
    ///
    /// Design Decision: Why Sort Every Time?
    /// - Alternative: Keep tasks in priority queue
    /// - Trade-off: Sorting is cheap for small N (2-10 tasks)
    /// - Benefit: Simpler code, easier to understand
    /// - If you have 50+ tasks, consider PriorityQueue&lt;IBackgroundTask, int&gt;
    /// </summary>
    public void ProcessBackgroundTasks()
    {
        if (_tasks.Count == 0)
            return;

        // Find tasks that are due to run
        var dueTasks = new List<IBackgroundTask>();
        foreach (var task in _tasks)
        {
            if (task.GetNextRunDelay() == 0)
            {
                dueTasks.Add(task);
            }
        }

        // No tasks due? Return early
        if (dueTasks.Count == 0)
            return;

        // Sort by priority (highest first)
        // Note: Descending order because higher priority number = higher priority
        dueTasks.Sort((a, b) => b.Priority.CompareTo(a.Priority));

        // Time budget tracking (if enabled)
        long startTime = TimeBudgetMicros > 0 ? GetMicroseconds() : 0;

        // Execute tasks in priority order
        foreach (var task in dueTasks)
        {
            // Check time budget (if enabled)
            if (TimeBudgetMicros > 0)
            {
                long elapsed = GetMicroseconds() - startTime;
                if (elapsed >= TimeBudgetMicros)
                {
                    // Budget exceeded - skip remaining tasks
                    // Lower priority tasks will be skipped
                    break;
                }
            }

            // Execute the task, use try-catch to prevent one task failure from stopping others
            try
            {
                task.Execute();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Fatal error: Executing background task '{task.GetType().Name}': {e}");
            }
        }
    }

    /// <summary>
    /// Gets the current time in microseconds for time budgeting.
    ///
    /// Uses Stopwatch.GetTimestamp() for high-resolution timing:
    /// - Sub-microsecond resolution on most platforms
    /// - Monotonic clock (doesn't go backwards)
    /// - Perfect for measuring small time intervals
    ///
    /// Note: This is more expensive than Environment.TickCount64
    /// (which is millisecond resolution), but necessary for microsecond budgeting.
    /// </summary>
    private long GetMicroseconds()
    {
        long timestamp = global::System.Diagnostics.Stopwatch.GetTimestamp();
        long frequency = global::System.Diagnostics.Stopwatch.Frequency;
        return (timestamp * 1_000_000) / frequency;
    }
}
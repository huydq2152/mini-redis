namespace MyRedis.Abstractions.Background;

/// <summary>
/// Represents a background maintenance task that runs periodically in the event loop.
///
/// Design Pattern: Strategy + Template Method
/// - Each task encapsulates its own scheduling logic and execution behavior
/// - BackgroundTaskManager coordinates all tasks without knowing their specifics
/// - New tasks can be added without modifying BackgroundTaskManager (Open/Closed Principle)
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
    /// Performance: Should be O(1) - just a time calculation or queue peek
    /// </summary>
    /// <returns>Milliseconds until task should execute (0 = now, positive = future)</returns>
    int GetNextRunDelay();

    /// <summary>
    /// Executes a bounded amount of background work.
    ///
    /// Time Budgeting Contract:
    /// - This method MUST complete quickly to avoid blocking the event loop
    /// - Use MaxWorkPerCycle to limit how much work you do per call
    ///
    /// Incremental Work Pattern:
    /// - If you have a lot of work to do, break it into small chunks
    /// - Process a bounded amount per call (e.g., 100 keys, 10 connections)
    /// - Return and let the event loop continue
    /// - You'll be called again on the next iteration to continue
    /// </summary>
    void Execute();

    /// <summary>
    /// Gets the maximum work units this task processes per Execute() call.
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

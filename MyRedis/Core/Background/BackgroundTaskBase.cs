using MyRedis.Abstractions.Background;
using MyRedis.Common.Helpers;

namespace MyRedis.Core.Background;

/// <summary>
/// Base class for background tasks providing common timing and scheduling functionality.
///
/// Purpose: Reduce Boilerplate Code
/// - Handles common timing calculations
/// - Implements standard scheduling patterns
/// - Provides template method structure
///
/// Typical Scheduling Patterns:
/// 1. Interval-based: Run every N milliseconds
/// 2. Work-driven: Run when work is available
/// 3. Hybrid: Run on interval OR when work is available (whichever is sooner)
///
/// Thread Safety: Not thread-safe. Assumes single-threaded event loop execution.
/// </summary>
public abstract class BackgroundTaskBase : IBackgroundTask
{
    /// <summary>
    /// Gets the task name for logging and monitoring.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the maximum work units processed per execution cycle.
    /// </summary>
    public int MaxWorkPerCycle { get; }

    /// <summary>
    /// Gets the priority of this task.
    /// </summary>
    public virtual int Priority => 50;

    /// <summary>
    /// Gets the interval between task executions in milliseconds.
    /// </summary>
    private int IntervalMs { get; }

    /// <summary>
    /// Stores the timestamp when this task should run next.
    /// </summary>
    private long _nextRunTime;

    /// <summary>
    /// Initializes a new interval-based background task.
    /// </summary>
    /// <param name="name">Human-readable task name</param>
    /// <param name="intervalMs">Milliseconds between executions (default: 100ms)</param>
    /// <param name="maxWorkPerCycle">Max work units per execution (default: 100)</param>
    /// <param name="priority">Task priority (default: 50)</param>
    protected BackgroundTaskBase(
        string name,
        int intervalMs = 100,
        int maxWorkPerCycle = 100,
        int priority = 50)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        IntervalMs = intervalMs;
        MaxWorkPerCycle = maxWorkPerCycle;

        // Initialize next run time to now (will run immediately on first iteration)
        _nextRunTime = TimeHelper.GetNow();
    }

    /// <summary>
    /// Calculates milliseconds until next execution .
    /// Implement default interval-based scheduling.
    /// </summary>
    public virtual int GetNextRunDelay()
    {
        var now = TimeHelper.GetNow();
        var delay = _nextRunTime - now;
        return delay > 0 ? (int)delay : 0;
    }

    /// <summary>
    /// Executes the background task work.
    /// </summary>
    public virtual void Execute()
    {
        // Execute the task-specific work
        ExecuteCore();

        // Schedule next run (interval-based)
        _nextRunTime = TimeHelper.GetNow() + IntervalMs;
    }

    /// <summary>
    /// Performs the actual task work.
    ///
    /// Execution Contract:
    /// - Complete quickly to avoid blocking event loop
    /// - Process at most MaxWorkPerCycle units of work
    /// - Update internal state as needed
    /// - Handle errors gracefully
    /// </summary>
    protected abstract void ExecuteCore();
}

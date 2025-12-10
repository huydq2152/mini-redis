using System.Threading.Channels;

namespace MyRedis.System;

/// <summary>
/// High-performance background task processor using .NET Channels for async workload offloading.
/// Implements the Producer-Consumer pattern to execute expensive operations without blocking
/// the main Redis event loop, ensuring consistent response times for client commands.
///
/// Architecture:
/// - Producer: Main thread submits tasks via Submit() method (O(1) operation)
/// - Consumer: Single background thread processes tasks from the queue
/// - Channel: Lock-free, high-performance queue implementation from System.Threading.Channels
///
/// Design Rationale:
/// Redis is inherently single-threaded for command processing to avoid complex synchronization.
/// However, some operations (like destroying large data structures) can block the main thread.
/// This BackgroundWorker offloads such expensive operations to maintain Redis responsiveness.
///
/// Use Cases in Redis:
/// - Asynchronous key deletion (UNLINK command behavior)
/// - Large object cleanup (e.g., destroying complex AVL trees)
/// - Expensive maintenance operations that don't require immediate completion
/// - Background persistence and replication tasks (future enhancements)
///
/// Performance Characteristics:
/// - Submit(): Nearly O(1), lock-free enqueue operation
/// - Processing: Sequential execution on dedicated thread
/// - Memory: Unbounded queue (consider memory limits for production)
/// - Throughput: Limited by consumer processing speed, not by queue operations
///
/// Thread Safety:
/// - Multiple producers supported (SingleWriter = false)
/// - Single consumer for simplicity and CPU efficiency (SingleReader = true)
/// - Channel implementation provides all necessary synchronization
///
/// Error Handling:
/// - Individual task failures are isolated and logged
/// - Worker continues processing despite individual task exceptions
/// - No retry logic (tasks are fire-and-forget)
/// </summary>
public class BackgroundWorker
{
    /// <summary>
    /// High-performance channel for task queuing with optimized configuration.
    /// 
    /// Channel Configuration:
    /// - Unbounded: Accepts unlimited task submissions (prevents blocking producers)
    /// - SingleReader = true: Only one consumer thread for CPU efficiency
    /// - SingleWriter = false: Multiple threads can submit tasks concurrently
    /// 
    /// The unbounded nature prevents producer blocking but requires monitoring
    /// in production environments to prevent memory exhaustion.
    /// </summary>
    private readonly Channel<Action> _channel = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }
    );

    /// <summary>
    /// Initializes a new BackgroundWorker and starts the consumer thread.
    /// The worker begins processing tasks immediately upon construction.
    /// </summary>
    /// <remarks>
    /// The consumer thread is started via Task.Run() which schedules it on the ThreadPool.
    /// This approach is preferred over creating dedicated Thread instances for better
    /// resource management and integration with the .NET async ecosystem.
    /// </remarks>
    public BackgroundWorker()
    {
        // Start the consumer thread immediately to begin processing queued tasks
        Task.Run(ProcessQueue);
    }

    /// <summary>
    /// Submits a task for asynchronous execution on the background thread.
    /// This method provides the Producer interface of the Producer-Consumer pattern.
    /// </summary>
    /// <param name="job">The action to execute asynchronously</param>
    /// <remarks>
    /// Performance Characteristics:
    /// - Nearly O(1) operation due to lock-free channel implementation
    /// - Non-blocking: Never waits for consumer or queue space
    /// - Thread-safe: Can be called from multiple threads concurrently
    /// 
    /// Usage Pattern:
    /// <code>
    /// backgroundWorker.Submit(() => {
    ///     // Expensive operation that shouldn't block main thread
    ///     DestroyLargeDataStructure(largeObject);
    /// });
    /// </code>
    /// 
    /// The submitted action will be executed sequentially with other tasks
    /// on the dedicated background thread, preserving execution order.
    /// </remarks>
    public void Submit(Action job)
    {
        _channel.Writer.TryWrite(job);
    }

    /// <summary>
    /// Consumer thread main loop that processes tasks from the channel queue.
    /// Implements the Consumer interface of the Producer-Consumer pattern.
    /// </summary>
    /// <returns>Task that completes when the worker is shut down</returns>
    /// <remarks>
    /// Processing Algorithm:
    /// 1. Asynchronously wait for tasks to become available
    /// 2. Process all immediately available tasks in a batch
    /// 3. Execute each task with individual error handling
    /// 4. Return to waiting state when queue is empty
    /// 
    /// Error Handling Strategy:
    /// - Individual task exceptions are caught and logged
    /// - Worker continues processing despite task failures
    /// - No retry mechanism - tasks are fire-and-forget
    /// 
    /// Performance Optimizations:
    /// - Batched processing reduces async/await overhead
    /// - Non-blocking wait using WaitToReadAsync()
    /// - Efficient TryRead() for draining available tasks
    /// 
    /// Shutdown Behavior:
    /// Worker will exit gracefully when the channel is completed
    /// (though no explicit shutdown mechanism is currently implemented).
    /// </remarks>
    private async Task ProcessQueue()
    {
        var reader = _channel.Reader;
            
        // Main consumer loop: wait for tasks and process them
        while (await reader.WaitToReadAsync())
        {
            // Batch process all immediately available tasks to reduce overhead
            while (reader.TryRead(out var job))
            {
                try
                {
                    // Execute the submitted task
                    // This is where expensive operations run without blocking main thread
                    job.Invoke();
                }
                catch (Exception ex)
                {
                    // Isolate task failures to prevent worker termination
                    // In production, consider more sophisticated logging
                    Console.WriteLine($"[BgWorker Error] {ex.Message}");
                }
            }
        }
    }
}
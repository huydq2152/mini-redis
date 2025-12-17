using System.Threading.Channels;
using MyRedis.System.Tasks;

namespace MyRedis.System.Workers;

/// <summary>
/// Dedicated worker for a single task category.
/// Each worker has its own queue, thread, and lifecycle management.
///
/// Features:
/// - Isolated processing (no cross-category blocking)
/// - Health monitoring with automatic failure detection
/// - Graceful shutdown with task draining
/// - Metrics collection for observability
/// </summary>
public sealed class CategoryWorker : IDisposable
{
    private readonly BackgroundTaskCategory _category;
    private readonly Channel<Action> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _processingTask;
    private readonly WorkerStatus _status;
    private readonly object _statusLock = new();
    private volatile bool _disposed;

    /// <summary>
    /// Configuration for this worker category.
    /// </summary>
    public class Options
    {
        /// <summary>Maximum queue depth before applying backpressure (0 = unbounded)</summary>
        public int MaxQueueSize { get; set; } = 0;

        /// <summary>Whether to use bounded channel with backpressure</summary>
        public bool UseBoundedChannel => MaxQueueSize > 0;

        /// <summary>Timeout for graceful shutdown (ms)</summary>
        public int ShutdownTimeoutMs { get; set; } = 5000;
    }

    public CategoryWorker(BackgroundTaskCategory category, Options? options = null)
    {
        _category = category;
        options ??= new Options();

        _status = new WorkerStatus { Category = category };
        _cts = new CancellationTokenSource();

        // Create channel based on configuration
        _channel = options.UseBoundedChannel
            ? Channel.CreateBounded<Action>(new BoundedChannelOptions(options.MaxQueueSize)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait // Apply backpressure
            })
            : Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        // Start worker with proper task tracking
        _processingTask = Task.Run(() => ProcessQueueAsync(_cts.Token));

        UpdateHealth(WorkerHealth.Healthy);
    }

    /// <summary>
    /// Submits a task to this worker's queue.
    /// </summary>
    /// <returns>True if submitted, false if queue is full or worker stopped</returns>
    public bool TrySubmit(Action task)
    {
        if (_disposed || _cts.IsCancellationRequested)
            return false;

        var submitted = _channel.Writer.TryWrite(task);

        if (submitted)
        {
            lock (_statusLock)
                _status.QueueDepth++;
        }

        return submitted;
    }

    /// <summary>
    /// Submits a task, waiting if queue is full (bounded channel only).
    /// </summary>
    public async ValueTask<bool> SubmitAsync(Action task, CancellationToken cancellation = default)
    {
        if (_disposed || _cts.IsCancellationRequested)
            return false;

        try
        {
            await _channel.Writer.WriteAsync(task, cancellation);

            lock (_statusLock)
                _status.QueueDepth++;

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets current worker status snapshot.
    /// </summary>
    public WorkerStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new WorkerStatus
            {
                Category = _status.Category,
                Health = _status.Health,
                TasksProcessed = _status.TasksProcessed,
                TasksFailed = _status.TasksFailed,
                QueueDepth = _status.QueueDepth,
                LastTaskCompletedAt = _status.LastTaskCompletedAt,
                LastTaskDurationMs = _status.LastTaskDurationMs,
                LastError = _status.LastError
            };
        }
    }

    /// <summary>
    /// Initiates graceful shutdown: stops accepting tasks, drains queue.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for task completion</param>
    /// <returns>True if shutdown completed cleanly, false if timed out</returns>
    public async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        if (_disposed)
            return true;

        UpdateHealth(WorkerHealth.ShuttingDown);

        // Signal no more tasks
        _channel.Writer.Complete();

        // Wait for processing to complete (with timeout)
        using var timeoutCts = new CancellationTokenSource(timeout);

        try
        {
            await _processingTask.WaitAsync(timeoutCts.Token);
            UpdateHealth(WorkerHealth.Stopped);
            return true;
        }
        catch (TimeoutException)
        {
            // Force cancellation if graceful shutdown timed out
            await _cts.CancelAsync();
            UpdateHealth(WorkerHealth.Failed);
            return false;
        }
        catch (OperationCanceledException)
        {
            UpdateHealth(WorkerHealth.Stopped);
            return true;
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellation)
    {
        var reader = _channel.Reader;
        var stopwatch = new global::System.Diagnostics.Stopwatch();

        try
        {
            while (await reader.WaitToReadAsync(cancellation))
            {
                while (reader.TryRead(out var task))
                {
                    if (cancellation.IsCancellationRequested)
                        break;

                    stopwatch.Restart();

                    try
                    {
                        task.Invoke();

                        lock (_statusLock)
                        {
                            _status.TasksProcessed++;
                            _status.QueueDepth--;
                            _status.LastTaskCompletedAt = Environment.TickCount64;
                            _status.LastTaskDurationMs = stopwatch.ElapsedMilliseconds;
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (_statusLock)
                        {
                            _status.TasksFailed++;
                            _status.QueueDepth--;
                            _status.LastError = ex;
                        }

                        // Log but continue processing
                        Console.WriteLine($"[{_category}Worker] Task failed: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            // Unexpected failure - worker is dead
            UpdateHealth(WorkerHealth.Failed);
            Console.WriteLine($"[{_category}Worker] FATAL: {ex}");

            lock (_statusLock)
                _status.LastError = ex;
        }
    }

    private void UpdateHealth(WorkerHealth health)
    {
        lock (_statusLock)
            _status.Health = health;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();

        // Note: Don't await _processingTask in Dispose (sync context)
        // Use ShutdownAsync for graceful shutdown
    }
}
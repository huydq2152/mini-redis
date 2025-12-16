using MyRedis.System.Tasks;

namespace MyRedis.System.Workers;

/// <summary>
/// Health and metrics information for a single worker.
/// </summary>
public class WorkerStatus
{
    public BackgroundTaskCategory Category { get; init; }
    public WorkerHealth Health { get; set; } = WorkerHealth.Starting;
    public long TasksProcessed { get; set; }
    public long TasksFailed { get; set; }
    public int QueueDepth { get; set; }
    public long LastTaskCompletedAt { get; set; }
    public long LastTaskDurationMs { get; set; }
    public Exception? LastError { get; set; }
    public bool IsAlive => Health == WorkerHealth.Healthy || Health == WorkerHealth.Degraded;
}
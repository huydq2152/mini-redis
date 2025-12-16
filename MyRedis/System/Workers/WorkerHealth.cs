namespace MyRedis.System.Workers;

/// <summary>
/// Represents the health status of a background worker.
/// </summary>
public enum WorkerHealth
{
    /// <summary>Worker is starting up</summary>
    Starting,

    /// <summary>Worker is running normally</summary>
    Healthy,

    /// <summary>Worker is processing but experiencing issues</summary>
    Degraded,

    /// <summary>Worker has stopped unexpectedly</summary>
    Failed,

    /// <summary>Worker is shutting down gracefully</summary>
    ShuttingDown,

    /// <summary>Worker has stopped cleanly</summary>
    Stopped
}
namespace MyRedis.System.Tasks;

/// <summary>
/// Categorizes background tasks by their resource characteristics.
/// Each category maps to a dedicated worker with appropriate threading model.
/// </summary>
public enum BackgroundTaskCategory
{
    /// <summary>
    /// CPU-bound memory operations: destroying large data structures,
    /// hashing, compression. Single thread to avoid allocator contention.
    /// </summary>
    LazyFree = 0,

    /// <summary>
    /// Disk I/O operations requiring durability guarantees: AOF fsync,
    /// RDB persistence. Dedicated thread ensures durability not blocked.
    /// </summary>
    Persistence = 1,

    /// <summary>
    /// Fast file operations: close(), unlink(). Usually fast but can
    /// block on NFS. Isolated to prevent blocking critical paths.
    /// </summary>
    FileOps = 2,

    /// <summary>
    /// Network I/O: replication, cluster communication.
    /// Can use async I/O for better scalability.
    /// </summary>
    Network = 3,

    /// <summary>
    /// Low-priority maintenance: metrics collection, log rotation,
    /// memory defragmentation. Can be delayed under load.
    /// </summary>
    Maintenance = 4
}
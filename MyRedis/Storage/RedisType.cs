namespace MyRedis.Storage;

/// <summary>
/// Enum representing the different data types that can be stored in Redis.
///
/// This enum is used to track the type of value stored in each RedisEntry,
/// enabling type checking and preventing operations on incompatible types.
///
/// Redis Type System:
/// Redis is NOT a plain key-value store - it's a data structure server.
/// Each key is bound to a specific data type, and operations are type-specific.
///
/// Type Safety:
/// - GET works on String type
/// - ZADD/ZRANGE work on SortedSet type
/// - Attempting to use the wrong command on a type returns WRONGTYPE error
///
/// Future Extensions:
/// As MyRedis grows, this enum will expand to include:
/// - List: For LPUSH/RPUSH/LRANGE commands
/// - Hash: For HSET/HGET/HGETALL commands
/// - Set: For SADD/SMEMBERS/SINTER commands
/// - Stream: For XADD/XREAD commands (Redis 5.0+)
/// </summary>
public enum RedisType : byte
{
    /// <summary>
    /// String type - the most basic Redis data type.
    ///
    /// Used by:
    /// - GET/SET commands
    /// - APPEND command
    /// - String manipulation commands
    ///
    /// Storage:
    /// - Stored as a C# string object (reference type)
    /// - Can contain any binary data (images, JSON, serialized objects)
    /// - Maximum size: 512MB (matching Redis proto-max-bulk-len)
    ///
    /// Special Cases:
    /// - Empty string: Valid value (different from key not existing)
    /// - Null: Treated as key not existing
    ///
    /// Note: For integer values, use RedisType.Integer instead for better performance.
    /// </summary>
    String = 0,

    /// <summary>
    /// Integer type - unboxed 64-bit signed integer (PERFORMANCE OPTIMIZED).
    ///
    /// NEW: Zero-Boxing Optimization
    /// - Stored inline in RedisValue union (no heap allocation)
    /// - Perfect for counters, IDs, timestamps
    /// - Zero GC pressure for INCR/DECR workloads
    ///
    /// Used by:
    /// - INCR/DECR commands (counters)
    /// - SET command with integer value
    /// - Integer-encoded values
    ///
    /// Storage:
    /// - Stored as long (8 bytes) in RedisValue union
    /// - NO boxing: value stored inline, not on heap
    /// - Range: -2^63 to 2^63-1
    ///
    /// Performance:
    /// - Before: INCR allocates 24 bytes per operation (boxing)
    /// - After: INCR zero allocations (inline storage)
    /// - 10K INCR/sec: Before = 240KB/sec garbage, After = 0 bytes
    ///
    /// Use Cases:
    /// - Counters (view counts, likes, votes)
    /// - IDs (user IDs, session IDs)
    /// - Timestamps (Unix epoch seconds)
    /// - Flags (0/1 boolean values)
    /// </summary>
    Integer = 1,

    /// <summary>
    /// Double type - unboxed 64-bit floating point (IEEE 754).
    ///
    /// NEW: Zero-Boxing Optimization
    /// - Stored inline in RedisValue union (no heap allocation)
    /// - Perfect for scores, ratings, measurements
    ///
    /// Used by:
    /// - INCRBYFLOAT command
    /// - Scientific/financial calculations
    /// - Precision measurements
    ///
    /// Storage:
    /// - Stored as double (8 bytes) in RedisValue union
    /// - NO boxing: value stored inline
    /// - IEEE 754 double precision
    ///
    /// Note: Sorted set scores are NOT stored as RedisType.Double.
    /// They're stored in the SortedSet object itself.
    /// </summary>
    Double = 2,

    /// <summary>
    /// Sorted Set type - a collection of unique members ordered by score.
    ///
    /// Used by:
    /// - ZADD command (add members with scores)
    /// - ZRANGE command (retrieve members by rank)
    /// - ZREM command (remove members)
    /// - ZSCORE command (get member's score)
    ///
    /// Storage:
    /// - Stored as a SortedSet object (AVL tree + dictionary)
    /// - Members are unique (like a set)
    /// - Each member has a numeric score (double)
    /// - Ordered by score (ascending by default)
    ///
    /// Use Cases:
    /// - Leaderboards (ranked by score)
    /// - Priority queues (sorted by priority)
    /// - Time-series data (sorted by timestamp)
    /// - Range queries (get top N, get elements between scores)
    /// </summary>
    SortedSet = 3,

    // Future data types (not yet implemented):
    // List = 4,        // Linked list for LPUSH/RPUSH
    // Hash = 5,        // Field-value pairs for HSET/HGET
    // Set = 6,         // Unordered unique values for SADD/SMEMBERS
    // Stream = 7,      // Append-only log for XADD/XREAD
}

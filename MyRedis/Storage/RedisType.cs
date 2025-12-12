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
    /// - INCR/DECR commands (strings containing integers)
    /// - APPEND command
    /// - String manipulation commands
    ///
    /// Storage:
    /// - Stored as a C# string object
    /// - Can contain any binary data (images, JSON, serialized objects)
    /// - Maximum size: 512MB (matching Redis proto-max-bulk-len)
    ///
    /// Special Cases:
    /// - Integer strings: Can be incremented/decremented (INCR/DECR)
    /// - Empty string: Valid value (different from key not existing)
    /// - Null: Treated as key not existing
    /// </summary>
    String = 0,

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
    SortedSet = 1,

    // Future data types (not yet implemented):
    // List = 2,        // Linked list for LPUSH/RPUSH
    // Hash = 3,        // Field-value pairs for HSET/HGET
    // Set = 4,         // Unordered unique values for SADD/SMEMBERS
    // Stream = 5,      // Append-only log for XADD/XREAD
}

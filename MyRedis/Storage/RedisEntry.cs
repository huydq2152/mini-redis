namespace MyRedis.Storage;

/// <summary>
/// Unified entry that combines value, expiration, and type metadata in a single object.
///
/// CRITICAL FIX: Resolves TOCTOU Race Condition and Double Hashing
///
/// Problem Before (Separate Dictionaries):
/// 1. Dictionary<string, object?> _store              // Data
/// 2. Dictionary<string, long> _keyExpirations        // Expiration
///
/// Issues:
/// - TOCTOU Race: IsExpired() check happens separate from Get()
///   → Background task can expire key between the two calls
///   → Client gets null even though key was "not expired" when checked
///
/// - Double Hashing: Each operation hashes the key twice:
///   → IsExpired(key): hash("key") → lookup _keyExpirations
///   → Get(key):       hash("key") → lookup _store
///   → Wastes ~50-100 CPU cycles per GET operation
///
/// Solution (Unified Entry):
/// Dictionary<string, RedisEntry> _db
///
/// Benefits:
/// - Single hash, single lookup: O(1) instead of O(2)
/// - Atomic expiration check: Check and get in one critical section
/// - Type safety: RedisType enum prevents WRONGTYPE errors
/// - Memory locality: All metadata in one cache line
/// - Extensible: Easy to add LRU, memory tracking, etc.
///
/// Design Inspired by Redis:
/// Redis uses a similar structure (simplified here for C#):
/// ```c
/// typedef struct redisObject {
///     unsigned type:4;        // RedisType enum
///     unsigned encoding:4;    // How the data is stored
///     unsigned lru:24;        // LRU time or LFU counter
///     int refcount;           // Reference counting
///     void *ptr;              // Pointer to actual data
/// } robj;
///
/// typedef struct redisDb {
///     dict *dict;             // Key → Value (redisObject)
///     dict *expires;          // Key → Expiration time
/// } redisDb;
/// ```
///
/// Our implementation merges expires into the entry itself (simpler, fewer lookups).
/// </summary>
public class RedisEntry
{
    /// <summary>
    /// The actual data value stored for this key.
    ///
    /// Polymorphic storage:
    /// - string: For String type (GET/SET commands)
    /// - SortedSet: For SortedSet type (ZADD/ZRANGE commands)
    /// - null: Valid value for String type (different from key not existing)
    ///
    /// Type Correspondence:
    /// - Type == RedisType.String     → Value is string or null
    /// - Type == RedisType.SortedSet  → Value is SortedSet object
    ///
    /// Future Optimization (Boxing Elimination):
    /// For numeric values (INCR/DECR), we currently box integers as strings.
    /// Future improvement: Add a union-like structure:
    /// ```csharp
    /// private object? _objectValue;   // String, SortedSet, etc.
    /// private long _int64Value;       // Unboxed integers
    /// ```
    /// This would eliminate GC pressure for counter workloads.
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    /// Absolute expiration timestamp in milliseconds (Environment.TickCount64).
    ///
    /// Expiration Semantics:
    /// - -1: No expiration (key is persistent)
    /// - > 0: Absolute time when key expires
    /// - Compared against Environment.TickCount64 for expiration checks
    ///
    /// Why Absolute Time (Not Relative TTL)?
    /// - Avoids recalculating expiration on every check
    /// - Single comparison: (now > ExpireAt) instead of (now > createdAt + ttl)
    /// - Matches Redis internal representation
    ///
    /// Lazy Expiration:
    /// When accessed (GET, EXISTS, etc.):
    /// ```csharp
    /// if (entry.ExpireAt > 0 && now > entry.ExpireAt) {
    ///     _db.Remove(key);  // Delete on access
    ///     return null;
    /// }
    /// ```
    ///
    /// Active Expiration:
    /// Background task periodically scans for expired keys using min-heap.
    ///
    /// Time Source:
    /// - Environment.TickCount64: Monotonic clock, milliseconds since boot
    /// - 64-bit: Won't overflow for ~292 million years
    /// - Not affected by system clock changes (DST, manual adjustments)
    /// </summary>
    public long ExpireAt { get; set; } = -1;

    /// <summary>
    /// The Redis data type of the value.
    ///
    /// Type Safety:
    /// Redis prevents type mismatches:
    /// - SET mykey "hello"     → Type = String
    /// - ZADD mykey 1.0 "m"    → Error: WRONGTYPE (can't ZADD on String)
    ///
    /// Type Checking Pattern:
    /// ```csharp
    /// if (entry.Type != RedisType.SortedSet) {
    ///     return Error("WRONGTYPE Operation against a key holding the wrong kind of value");
    /// }
    /// ```
    ///
    /// Type Conversion:
    /// Redis allows overwriting a key with a different type:
    /// - SET mykey "hello"      → Type = String
    /// - ZADD mykey 1.0 "m"     → Type = SortedSet (String value discarded)
    /// This is intentional and matches Redis behavior.
    ///
    /// Why Store Type?
    /// - Prevents invalid operations (runtime type safety)
    /// - Enables optimizations (different encodings per type)
    /// - Debugging: Easily see what type a key holds
    /// - Future: Type-specific memory reporting
    /// </summary>
    public RedisType Type { get; set; }

    /// <summary>
    /// Creates a new Redis entry for a string value.
    ///
    /// This is the most common case (90%+ of Redis keys are strings).
    /// Provides a convenient factory method to avoid manually setting Type.
    ///
    /// Usage:
    /// ```csharp
    /// _db[key] = RedisEntry.String(value);
    /// ```
    /// </summary>
    /// <param name="value">The string value (can be null)</param>
    /// <param name="expireAt">Optional expiration timestamp (-1 for no expiration)</param>
    /// <returns>A new RedisEntry configured for String type</returns>
    public static RedisEntry String(string? value, long expireAt = -1)
    {
        return new RedisEntry
        {
            Value = value,
            ExpireAt = expireAt,
            Type = RedisType.String
        };
    }

    /// <summary>
    /// Creates a new Redis entry for a sorted set value.
    ///
    /// Used by ZADD command to create or replace a key with a sorted set.
    ///
    /// Usage:
    /// ```csharp
    /// var sortedSet = new SortedSet();
    /// sortedSet.Add("member", 1.0);
    /// _db[key] = RedisEntry.SortedSet(sortedSet);
    /// ```
    /// </summary>
    /// <param name="sortedSet">The SortedSet object</param>
    /// <param name="expireAt">Optional expiration timestamp (-1 for no expiration)</param>
    /// <returns>A new RedisEntry configured for SortedSet type</returns>
    public static RedisEntry SortedSet(Storage.DataStructures.SortedSet sortedSet, long expireAt = -1)
    {
        return new RedisEntry
        {
            Value = sortedSet,
            ExpireAt = expireAt,
            Type = RedisType.SortedSet
        };
    }

    /// <summary>
    /// Checks if this entry has expired.
    ///
    /// Expiration Check:
    /// - ExpireAt == -1: Never expires (returns false)
    /// - now > ExpireAt: Expired (returns true)
    /// - now <= ExpireAt: Still valid (returns false)
    ///
    /// Usage in DataStore.Get():
    /// ```csharp
    /// if (entry.IsExpired()) {
    ///     _db.Remove(key);  // Lazy deletion
    ///     return null;
    /// }
    /// return entry.Value;
    /// ```
    ///
    /// Performance: Inline method, single comparison, ~5 CPU cycles.
    /// </summary>
    /// <returns>True if expired, false otherwise</returns>
    public bool IsExpired()
    {
        // No expiration set
        if (ExpireAt < 0)
            return false;

        // Check if current time exceeds expiration time
        return Environment.TickCount64 > ExpireAt;
    }

    /// <summary>
    /// Gets the remaining TTL in milliseconds.
    ///
    /// Return Values:
    /// - null: No expiration (persistent key)
    /// - 0: Expired (or expiring right now)
    /// - > 0: Milliseconds until expiration
    ///
    /// Used by TTL command:
    /// ```csharp
    /// long? ttl = entry.GetTtl();
    /// if (ttl == null) return -1;  // No expiration
    /// if (ttl == 0) return -2;     // Key expired
    /// return ttl.Value;
    /// ```
    /// </summary>
    /// <returns>TTL in milliseconds, or null if no expiration</returns>
    public long? GetTtl()
    {
        // No expiration
        if (ExpireAt < 0)
            return null;

        // Calculate remaining time
        long remaining = ExpireAt - Environment.TickCount64;

        // Return at least 0 (negative means already expired)
        return remaining > 0 ? remaining : 0;
    }
}

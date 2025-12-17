using MyRedis.Abstractions.Storage;
using MyRedis.Storage.DataStructures;
using MyRedis.Storage.Models;

namespace MyRedis.Services.Storage;

/// <summary>
/// In-memory implementation of the Redis data store using a unified entry structure.
/// This is the core storage engine where all Redis data is persisted during server runtime.
///
/// ARCHITECTURE: Unified Entry Pattern (Single unified storage)
/// - Dictionary<string, RedisEntry> _db
///
/// Benefits:
/// Single hash calculation per operation
/// Atomic expiration check (no race condition)
/// Type safety (RedisType enum prevents WRONGTYPE errors)
/// Memory locality (all metadata in one cache line)
/// Extensible (easy to add LRU, refcount, memory tracking)
///
/// Architecture:
/// - Uses Dictionary<string, RedisEntry> as the underlying storage mechanism
/// - RedisEntry combines: Value + ExpireAt + Type in a single object
/// - All operations are O(1) average case (Dictionary hash table performance)
/// - Exception: GetAllKeys() is O(n) where n is the total number of keys
///
/// Data Types Supported:
/// - RedisType.String: Simple key-value pairs (GET/SET commands)
/// - RedisType.SortedSet: For sorted set operations (ZADD/ZRANGE commands)
/// - Future: List, Hash, Set, Stream
///
/// Thread Safety Strategy: LOCK-FREE (Single-Threaded Event Loop)
///
/// IMPORTANT: This class is NOT thread-safe and does NOT use locks.
///
/// WHY NO LOCKS?
/// MyRedis uses a single-threaded event loop architecture (like Redis in C):
/// - All operations run sequentially on the event loop thread
/// - Background tasks (expiration, idle cleanup) run on the SAME thread
/// - No concurrent access = no race conditions = locks are unnecessary overhead
///
/// PERFORMANCE BENEFIT:
/// - Background expiration: faster without locks
/// - Command processing: faster per operation
/// - P99 latency: Lower variance (no lock convoy effect)
///
/// FUTURE MULTI-THREADING SAFETY:
/// If you later add multi-threading (e.g., background expiration on separate thread),
/// you MUST add synchronization. Choose based on your concurrency pattern:
///
/// Option A: Coarse-Grained Lock (Simplest)
/// Pros: Simple, correct
/// Cons: Lock contention under high concurrency
///
/// Option B: ConcurrentDictionary (Lock-Free Reads)
/// Pros: Better read performance under concurrency
/// Cons: Expiration updates need careful atomic operations
///
/// Option C: Sharded Locks (High Concurrency)
/// Pros: Reduces lock contention (32× parallelism)
/// Cons: More complex, batch operations tricky
///
/// Option D: Reader-Writer Lock (Read-Heavy Workloads)
/// Pros: Multiple concurrent reads, exclusive writes
/// Cons: Write starvation possible, higher overhead than simple lock
///
/// RECOMMENDATION FOR FUTURE:
/// - Start with Option A (coarse lock) for correctness
/// - Profile under load to identify bottlenecks
/// - Upgrade to Option B or C only if lock contention is measured >5%
///
/// Memory Management:
/// - Keys and values are kept in memory for fast access
/// - No automatic memory limits (future: LRU eviction policy)
/// - Garbage collection handles cleanup when keys are removed
/// - Future: Memory usage monitoring and reporting
///
/// Thread Safety: NOT THREAD-SAFE (single-threaded event loop only)
/// </summary>
public class InMemoryDataStore : IDataStore
{
    /// <summary>
    /// The unified dictionary that stores all Redis data with metadata.
    ///
    /// Structure:
    /// - Key: Redis key name (string)
    /// - Value: RedisEntry containing:
    ///   - Value: The actual data (string, SortedSet, etc.)
    ///   - ExpireAt: Expiration timestamp (-1 = no expiration)
    ///   - Type: RedisType enum (String, SortedSet, etc.)
    ///
    /// Performance Characteristics:
    /// - O(1) average case for all operations (hash table lookup)
    /// - Single hash calculation per operation (vs. 2× in old architecture)
    /// - Atomic expiration checks (no TOCTOU race)
    /// - Cache-friendly (all metadata in one object)
    ///
    /// Memory Layout (per entry):
    /// - Dictionary entry: ~48 bytes overhead (key pointer, hash, next pointer)
    /// - RedisEntry object: ~40 bytes (object header + 3 fields)
    /// - Total: ~88 bytes + key string + value object
    /// 
    /// TODO: At scale (10M+ keys), standard Dictionary causes LOH fragmentation and GC pauses.
    /// Future Upgrade: Redis in C use Incremental Resizing solution
    /// With C#, Incremental Resizing solution need manual hash table implementation instead of Dictionary.
    /// Can read more some simple best practice for optimizing Dictionary performance in:
    /// https://coldfusion-example.blogspot.com/2025/02/boost-efficiency-deep-dive-into-c.html
    /// Better solution: implement Sharded Dictionary (1024 partitions) to bound resize latency.
    /// </summary>
    private readonly Dictionary<string, RedisEntry> _db = new();

    /// <summary>
    /// Retrieves a value by key with automatic lazy expiration handling.
    ///
    /// Atomic Expiration Check
    /// 
    /// Lazy Expiration:
    /// Keys are checked for expiration when accessed (passive expiration).
    /// If expired, the key is immediately deleted and null is returned.
    /// This ensures:
    /// - No expired data is ever returned to clients
    /// - Memory is freed as soon as expired keys are accessed
    /// - Complements active expiration (background cleanup)
    ///
    /// Performance:
    /// - Single hash calculation (vs. 2× in old architecture)
    /// - Single dictionary lookup (vs. 2× lookups)
    /// - Inline expiration check (~5 CPU cycles)
    /// - Total: ~50-100 CPU cycles saved per GET with TTL
    ///
    
    /// This method modifies _db (lazy deletion) without synchronization.
    /// Safe because event loop processes one operation at a time.
    /// </summary>
    /// <param name="key">The Redis key to retrieve</param>
    /// <returns>
    /// The stored value if the key exists and is not expired.
    /// Null if:
    /// - Key doesn't exist
    /// - Key has expired (and is now deleted)
    /// - Value is explicitly null
    /// </returns>
    public object? Get(string key)
    {
        // Try to get the entry
        if (!_db.TryGetValue(key, out var entry))
            return null;

        // Atomic expiration check
        if (entry.IsExpired())
        {
            // Lazy expiration: Delete on access
            _db.Remove(key);
            return null;
        }

        // Key exists and is not expired
        // Extract value from RedisValue union (may box integers/doubles)
        return entry.Value.AsObject();
    }

    /// <summary>
    /// Retrieves a value by key with type checking, expiration handling, and safe casting.
    ///
    /// This method provides:
    /// 1. Atomic expiration check
    /// 2. Type safety (returns null if wrong type)
    /// 3. Lazy expiration (deletes expired keys on access)
    ///
    /// Used by command handlers that expect a specific type
    /// </summary>
    /// <typeparam name="T">The expected type (must be a reference type)</typeparam>
    /// <param name="key">The Redis key to retrieve</param>
    /// <returns>
    /// The typed value if found, type-compatible, and not expired.
    /// Null in these cases:
    /// - Key doesn't exist
    /// - Key has expired (now deleted)
    /// - Value is explicitly null
    /// - Value exists but is not assignable to type T (WRONGTYPE)
    /// </returns>
    /// <remarks>
    /// Type Safety Pattern
    /// Performance:
    /// - Single hash, single lookup (vs. 2× in old architecture)
    /// - Inline expiration check (~5 cycles)
    /// - Type check via 'is' operator (~10 cycles)
    /// - Total: 50-100 CPU cycles saved per typed GET with TTL
    /// </remarks>
    public T? Get<T>(string key) where T : class
    {
        // NO LOCK - Single-threaded event loop guarantees sequential access
        //
        // FUTURE MULTI-THREADING: If adding background threads, wrap with:
        // lock (_lock) { ... }

        // Try to get the entry
        if (!_db.TryGetValue(key, out var entry))
            return null; // Key doesn't exist

        // Atomic expiration check
        if (entry.IsExpired())
        {
            // Lazy expiration
            _db.Remove(key);
            return null;
        }

        // Type-safe cast - extract value from RedisValue union
        var value = entry.Value.AsObject();
        return value as T;
    }

    /// <summary>
    /// Stores a value with explicit type and optional expiration.
    ///
    /// This is the recommended method for command handlers to use.
    /// Ensures proper type tracking and expiration management.
    /// </summary>
    /// <param name="key">The Redis key</param>
    /// <param name="value">The value to store</param>
    /// <param name="type">The Redis data type</param>
    /// <param name="expireAt">Expiration timestamp (-1 for no expiration)</param>
    public void SetWithType(string key, object? value, RedisType type, long expireAt = -1)
    {
        // Create appropriate RedisEntry based on type
        var entry = type switch
        {
            RedisType.Integer => RedisEntry.Integer((long)value!, expireAt),
            RedisType.Double => RedisEntry.Double((double)value!, expireAt),
            RedisType.String => RedisEntry.String(value as string, expireAt),
            RedisType.SortedSet => RedisEntry.SortedSet((SortedSet)value!, expireAt),
            _ => throw new ArgumentException($"Unsupported Redis type: {type}")
        };

        _db[key] = entry;
    }

    /// <summary>
    /// Removes a key and its associated RedisEntry from the data store.
    ///
    /// SIMPLIFIED: With unified entry, this is the ONLY method needed for deletion.
    /// No need to coordinate with ExpirationService - expiration is part of the entry.
    /// </summary>
    /// <param name="key">The Redis key to remove</param>
    /// <returns>
    /// True if the key existed and was successfully removed.
    /// False if the key didn't exist (no-op).
    /// </returns>
    /// <remarks>
    /// What Gets Removed:
    /// - The key itself
    /// - The value (freed for GC)
    /// - Expiration metadata (no separate cleanup needed)
    /// - Type information
    ///
    /// Performance: O(1) average case for Dictionary.Remove().
    /// </remarks>
    public bool Remove(string key)
    {
        // PERFORMANCE: This is called up to 100× in ProcessExpiredKeys()
        // Lock overhead would be ~10μs (100 locks × 100ns each)
        return _db.Remove(key);
    }

    /// <summary>
    /// Checks whether a key exists and is not expired.
    ///
    /// ATOMIC CHECK: Combines existence and expiration checks in one operation.
    ///
    /// Lazy Expiration:
    /// If the key exists but has expired, it's immediately deleted.
    /// This ensures Exists() never returns true for an expired key.
    /// </summary>
    /// <param name="key">The Redis key to check for existence</param>
    /// <returns>
    /// True if the key exists and is not expired.
    /// False if:
    /// - Key doesn't exist
    /// - Key has expired (and is now deleted)
    /// </returns>
    /// <remarks>
    /// Used by: EXISTS command, TTL command, conditional operations.
    ///
    /// Performance:
    /// - O(1) dictionary lookup
    /// - Inline expiration check (~5 cycles)
    /// - Total: ~50-60 CPU cycles per call
    /// </remarks>
    public bool Exists(string key)
    {
        // Check if key exists
        if (!_db.TryGetValue(key, out var entry))
            return false; // Doesn't exist

        // Atomic expiration check
        if (entry.IsExpired())
        {
            // Lazy expiration
            _db.Remove(key);
            return false;
        }

        return true; // Exists and not expired
    }

    /// <summary>
    /// Returns all keys currently stored in the data store.
    ///
    /// WARNING: This is a potentially dangerous operation at scale.
    ///
    /// Performance Issues:
    /// - O(n) operation where n is the number of keys
    /// - 10M keys = 80MB+ allocation for key list
    /// - Blocks all other operations while holding lock
    /// - Can cause OutOfMemoryException with large datasets
    ///
    /// Redis Best Practice:
    /// - KEYS command is DEPRECATED in production Redis
    /// - Use SCAN command instead (cursor-based iteration)
    /// - SCAN returns batches of ~10 keys per call (bounded memory)
    ///
    /// Primary Uses:
    /// - KEYS command (DANGEROUS - should warn user or disable)
    /// - Background maintenance operations (use with caution)
    /// - Debugging and monitoring tools (non-production only)
    /// - Database backup/export operations
    ///
    /// Note About Expiration:
    /// - Returns ALL keys, including expired ones
    /// - Lazy expiration happens on access, not on iteration
    /// - Callers should check entry.IsExpired() if filtering needed
    /// - Active expiration cleanup runs in background
    /// 
    /// Returns a snapshot (ToList()) to avoid issues if iteration is interrupted.
    /// </remarks>
    public IEnumerable<string> GetAllKeys()
    {
        // NOTE: ToList() creates a snapshot to prevent modification during iteration
        return _db.Keys.ToList();
    }

    /// <summary>
    /// Gets the total number of keys currently stored in the data store.
    /// Useful for monitoring, statistics, and capacity planning.
    /// </summary>
    /// <remarks>
    /// Performance: O(1) operation using Dictionary.Count property.
    ///
    /// Important Notes:
    /// - May include expired keys that haven't been cleaned up yet
    /// - Lazy expiration only happens on access (GET, EXISTS, etc.)
    /// - Active expiration runs in background but may lag
    /// - The count reflects the current in-memory state
    /// - Used for monitoring server memory usage and key distribution
    ///
    /// Typical Uses:
    /// - DBSIZE command (Redis compatibility)
    /// - Server monitoring and alerting
    /// - Capacity planning and resource management
    /// - Performance benchmarking
    ///
    /// For Accurate Count (excluding expired keys):
    /// Would need O(n) iteration to check each entry.IsExpired().
    /// Not implemented due to performance cost.
    /// Use active expiration + lazy expiration to minimize stale keys.
    /// Atomic read of Dictionary.Count (no race possible on single thread).
    ///
    /// FUTURE MULTI-THREADING: Wrap with lock (_lock) { return _db.Count; }
    /// </remarks>
    public int Count => _db.Count;
}
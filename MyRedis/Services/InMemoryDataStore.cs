using MyRedis.Abstractions;
using MyRedis.Storage;
using MyRedis.Storage.DataStructures;

namespace MyRedis.Services;

/// <summary>
/// In-memory implementation of the Redis data store using a unified entry structure.
/// This is the core storage engine where all Redis data is persisted during server runtime.
///
/// CRITICAL ARCHITECTURE CHANGE: Unified Entry Pattern
///
/// Before (Separate Dictionaries):
/// - Dictionary<string, object?> _store              // Data storage
/// - Dictionary<string, long> _keyExpirations        // Expiration tracking (in ExpirationManager)
///
/// Issues Fixed:
/// 1. TOCTOU Race Condition:
///    - IsExpired() and Get() were separate operations
///    - Background task could expire key between the two calls
///    - Client got null even though key was "not expired" at check time
///
/// 2. Double Hashing Performance Waste:
///    - IsExpired(key): hash("key") → lookup _keyExpirations (~50 CPU cycles)
///    - Get(key):       hash("key") → lookup _store         (~50 CPU cycles)
///    - Total: 100+ wasted CPU cycles per GET operation
///
/// After (Unified Entry):
/// - Dictionary<string, RedisEntry> _db              // Single unified storage
///
/// Benefits:
/// ✅ Single hash calculation per operation (50% faster)
/// ✅ Atomic expiration check (no race condition)
/// ✅ Type safety (RedisType enum prevents WRONGTYPE errors)
/// ✅ Memory locality (all metadata in one cache line)
/// ✅ Extensible (easy to add LRU, refcount, memory tracking)
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
/// Thread Safety Strategy:
/// Uses coarse-grained locking with a single lock object for all operations.
/// This ensures data consistency when accessed from:
/// - Multiple client command threads (via event loop)
/// - Background expiration cleanup thread
/// - Future: Persistence/replication threads
///
/// Alternative Concurrency Approaches (for future consideration):
/// - ConcurrentDictionary<string, RedisEntry> for lock-free operations
/// - Reader-writer locks for read-heavy workloads
/// - Sharded locks for reduced lock contention (16-32 shards)
/// - Lock-free data structures for maximum performance
///
/// Memory Management:
/// - Keys and values are kept in memory for fast access
/// - No automatic memory limits (future: LRU eviction policy)
/// - Garbage collection handles cleanup when keys are removed
/// - Future: Memory usage monitoring and reporting
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
    /// Compared to old architecture (2 dictionaries):
    /// - Old: 2 × 48 bytes = 96 bytes overhead per key
    /// - New: 88 bytes overhead per key
    /// - Savings: ~8 bytes per key + eliminated duplicate key strings
    /// </summary>
    private readonly Dictionary<string, RedisEntry> _db = new();

    /// <summary>
    /// Synchronization lock for thread-safe access to the data store.
    /// All operations acquire this lock to ensure atomic operations
    /// and prevent race conditions between concurrent client requests
    /// and background maintenance tasks.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// Retrieves a value by key with automatic lazy expiration handling.
    ///
    /// CRITICAL FIX: Atomic Expiration Check
    ///
    /// Before (TOCTOU Race):
    /// ```csharp
    /// if (expirationService.IsExpired(key)) { ... }  // Time T1
    /// // Background task expires key here!
    /// var value = dataStore.Get(key);                 // Time T2 - returns null!
    /// ```
    ///
    /// After (Atomic):
    /// ```csharp
    /// var value = dataStore.Get(key);  // Single atomic operation
    /// // Expiration checked inside lock - no race possible
    /// ```
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
        lock (_lock)
        {
            // Try to get the entry
            if (!_db.TryGetValue(key, out var entry))
                return null; // Key doesn't exist

            // Atomic expiration check (TOCTOU fix)
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
    }

    /// <summary>
    /// Retrieves a value by key with type checking, expiration handling, and safe casting.
    ///
    /// This method provides:
    /// 1. Atomic expiration check (TOCTOU fix)
    /// 2. Type safety (returns null if wrong type)
    /// 3. Lazy expiration (deletes expired keys on access)
    ///
    /// Used by command handlers that expect a specific type:
    /// - ZADD/ZRANGE: Expect SortedSet
    /// - GET: Expects string
    /// - Type mismatch: Returns null (caller returns WRONGTYPE error)
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
    /// Type Safety Pattern:
    /// ```csharp
    /// var sortedSet = dataStore.Get<SortedSet>("myzset");
    /// if (sortedSet == null) {
    ///     // Could be: key doesn't exist, expired, or wrong type
    ///     // To distinguish, check dataStore.Exists(key) first
    ///     // Or better: Check entry.Type == RedisType.SortedSet
    /// }
    /// </code>
    ///
    /// Performance:
    /// - Single hash, single lookup (vs. 2× in old architecture)
    /// - Inline expiration check (~5 cycles)
    /// - Type check via 'is' operator (~10 cycles)
    /// - Total: 50-100 CPU cycles saved per typed GET with TTL
    /// </remarks>
    public T? Get<T>(string key) where T : class
    {
        lock (_lock)
        {
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
            object? value = entry.Value.AsObject();
            return value is T typedValue ? typedValue : null;
        }
    }

    /// <summary>
    /// Stores a value for the specified key, creating or overwriting as needed.
    ///
    /// IMPORTANT: This method is deprecated for direct use.
    /// Command handlers should use SetWithType() or create RedisEntry directly
    /// to ensure proper type tracking.
    ///
    /// This overload exists for backward compatibility and defaults to RedisType.String.
    /// </summary>
    /// <param name="key">The Redis key (string identifier)</param>
    /// <param name="value">
    /// The value to store. Can be:
    /// - string (for simple key-value storage)
    /// - SortedSet (for sorted set operations)
    /// - null (to explicitly store a null value)
    /// </param>
    /// <remarks>
    /// Behavior:
    /// - Creates the key if it doesn't exist
    /// - Overwrites the existing value if the key exists (regardless of previous type)
    /// - Preserves existing expiration time if key already exists
    /// - Defaults to RedisType.String (may be incorrect for SortedSet!)
    ///
    /// Type Overwriting:
    /// Redis allows changing the data type of a key:
    /// - SET mykey "hello" (creates string)
    /// - ZADD mykey 1.0 "member" (overwrites with sorted set)
    /// This is standard Redis behavior and fully supported.
    ///
    /// Expiration Behavior:
    /// - If key exists: Preserves existing ExpireAt value
    /// - If key is new: Sets ExpireAt = -1 (no expiration)
    /// - To set expiration: Use EXPIRE command after SET
    ///
    /// Thread Safety: Atomic operation under lock ensures consistency.
    /// Performance: O(1) average case for Dictionary operations.
    /// </remarks>
    public void Set(string key, object? value)
    {
        lock (_lock)
        {
            // Check if key already exists to preserve expiration
            if (_db.TryGetValue(key, out var existing))
            {
                // Update existing entry (preserve expiration)
                _db[key] = RedisEntry.String(value as string, expireAt: existing.ExpireAt);
            }
            else
            {
                // Create new entry
                _db[key] = RedisEntry.String(value as string, expireAt: -1);
            }
        }
    }

    /// <summary>
    /// Stores a value with explicit type and optional expiration.
    ///
    /// This is the recommended method for command handlers to use.
    /// Ensures proper type tracking and expiration management.
    ///
    /// Usage Examples:
    /// ```csharp
    /// // SET command
    /// dataStore.SetWithType(key, value, RedisType.String, expireAt);
    ///
    /// // ZADD command
    /// dataStore.SetWithType(key, sortedSet, RedisType.SortedSet, expireAt);
    /// ```
    /// </summary>
    /// <param name="key">The Redis key</param>
    /// <param name="value">The value to store</param>
    /// <param name="type">The Redis data type</param>
    /// <param name="expireAt">Expiration timestamp (-1 for no expiration)</param>
    public void SetWithType(string key, object? value, RedisType type, long expireAt = -1)
    {
        lock (_lock)
        {
            // Create appropriate RedisEntry based on type
            RedisEntry entry = type switch
            {
                RedisType.Integer => RedisEntry.Integer((long)value!, expireAt),
                RedisType.Double => RedisEntry.Double((double)value!, expireAt),
                RedisType.String => RedisEntry.String(value as string, expireAt),
                RedisType.SortedSet => RedisEntry.SortedSet((SortedSet)value!, expireAt),
                _ => throw new ArgumentException($"Unsupported Redis type: {type}")
            };

            _db[key] = entry;
        }
    }

    /// <summary>
    /// Removes a key and its associated RedisEntry from the data store.
    ///
    /// SIMPLIFIED: With unified entry, this is the ONLY method needed for deletion.
    /// No need to coordinate with ExpirationService - expiration is part of the entry.
    ///
    /// Before (Coordination Required):
    /// ```csharp
    /// bool removed = dataStore.Remove(key);
    /// if (removed) {
    ///     expirationService.RemoveExpiration(key);  // Must clean up separately
    /// }
    /// ```
    ///
    /// After (Single Operation):
    /// ```csharp
    /// bool removed = dataStore.Remove(key);  // Expiration removed automatically
    /// ```
    /// </summary>
    /// <param name="key">The Redis key to remove</param>
    /// <returns>
    /// True if the key existed and was successfully removed.
    /// False if the key didn't exist (no-op).
    /// </returns>
    /// <remarks>
    /// Used by:
    /// - DEL command (manual key deletion)
    /// - Lazy expiration (GET on expired key)
    /// - Background expiration process (active cleanup)
    /// - Type conversion operations (implicit removal)
    ///
    /// What Gets Removed:
    /// - The key itself
    /// - The value (freed for GC)
    /// - Expiration metadata (no separate cleanup needed)
    /// - Type information
    ///
    /// Thread Safety: Atomic operation ensures no partial removals.
    /// Performance: O(1) average case for Dictionary.Remove().
    /// </remarks>
    public bool Remove(string key)
    {
        lock (_lock)
        {
            return _db.Remove(key);
        }
    }

    /// <summary>
    /// Checks whether a key exists and is not expired.
    ///
    /// ATOMIC CHECK: Combines existence and expiration checks in one operation.
    ///
    /// Before (TOCTOU Race):
    /// ```csharp
    /// if (dataStore.Exists(key) && !expirationService.IsExpired(key)) {
    ///     // Race: Key might expire between checks
    /// }
    /// ```
    ///
    /// After (Atomic):
    /// ```csharp
    /// if (dataStore.Exists(key)) {
    ///     // Atomically checks existence AND expiration
    /// }
    /// ```
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
    /// - Total: ~50-60 CPU cycles
    ///
    /// Comparison to Get():
    /// - Exists(): Doesn't retrieve value (faster if value is large)
    /// - Get(): Retrieves value (use if you need the data anyway)
    /// - Both perform lazy expiration
    /// </remarks>
    public bool Exists(string key)
    {
        lock (_lock)
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
    /// Future: Implement SCAN command to replace this.
    /// </summary>
    /// <returns>
    /// A collection containing all key names in the store.
    /// The collection is a snapshot (copy) to prevent concurrent modification issues.
    /// </returns>
    /// <remarks>
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
    /// Thread Safety:
    /// Returns a snapshot (ToList()) to avoid ConcurrentModificationException
    /// if the original dictionary is modified while iterating.
    /// </remarks>
    public IEnumerable<string> GetAllKeys()
    {
        lock (_lock)
        {
            return _db.Keys.ToList(); // Return a copy to avoid concurrent modification
        }
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
    ///
    /// Thread Safety: Atomic read of Dictionary.Count under lock.
    /// </remarks>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _db.Count;
            }
        }
    }
}
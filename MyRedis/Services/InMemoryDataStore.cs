using MyRedis.Abstractions;

namespace MyRedis.Services;

/// <summary>
/// In-memory implementation of the Redis data store using a thread-safe dictionary.
/// This is the core storage engine where all Redis data is persisted during server runtime.
///
/// Architecture:
/// - Uses Dictionary<string, object?> as the underlying storage mechanism
/// - Supports polymorphic values (strings, sorted sets, future: lists, hashes)
/// - All operations are O(1) average case (Dictionary hash table performance)
/// - Exception: GetAllKeys() is O(n) where n is the total number of keys
///
/// Data Types Supported:
/// - string: Simple key-value pairs (GET/SET commands)
/// - SortedSet: For sorted set operations (ZADD/ZRANGE commands)
/// - null: Explicitly stored null values
/// - Future: Lists, Hashes, Sets, and other Redis data structures
///
/// Thread Safety Strategy:
/// Uses coarse-grained locking with a single lock object for all operations.
/// This ensures data consistency when accessed from:
/// - Multiple client command threads (via event loop)
/// - Background expiration cleanup thread
/// - Future: Persistence/replication threads
///
/// Alternative Concurrency Approaches (for future consideration):
/// - ConcurrentDictionary<string, object?> for lock-free operations
/// - Reader-writer locks for read-heavy workloads
/// - Sharded locks for reduced lock contention
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
    /// The underlying dictionary that stores all Redis key-value data.
    /// Keys are Redis key names (strings), values are polymorphic objects
    /// representing different Redis data types.
    /// </summary>
    private readonly Dictionary<string, object?> _store = new();
    
    /// <summary>
    /// Synchronization lock for thread-safe access to the data store.
    /// All operations acquire this lock to ensure atomic operations
    /// and prevent race conditions between concurrent client requests
    /// and background maintenance tasks.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// Retrieves a value by key without type checking or casting.
    /// This is the base retrieval method used when the expected type is unknown
    /// or when type checking will be performed by the caller.
    /// </summary>
    /// <param name="key">The Redis key to retrieve</param>
    /// <returns>
    /// The stored value if the key exists, null if the key doesn't exist
    /// or if null was explicitly stored as the value.
    /// </returns>
    /// <remarks>
    /// Performance: O(1) average case due to Dictionary hash table lookup.
    /// Thread-safe: Acquires read lock to ensure consistent state.
    /// 
    /// Use cases:
    /// - When the caller will perform their own type checking
    /// - When the expected type is unknown (e.g., generic operations)
    /// - As a building block for the generic Get<T>() method
    /// </remarks>
    public object? Get(string key)
    {
        lock (_lock)
        {
            return _store.TryGetValue(key, out var value) ? value : null;
        }
    }

    /// <summary>
    /// Retrieves a value by key with type checking and safe casting.
    /// Provides type safety by returning null if the key doesn't exist
    /// or if the stored value is not compatible with the requested type.
    /// </summary>
    /// <typeparam name="T">The expected type (must be a reference type)</typeparam>
    /// <param name="key">The Redis key to retrieve</param>
    /// <returns>
    /// The typed value if found and type-compatible, null otherwise.
    /// Null is returned in these cases:
    /// - Key doesn't exist
    /// - Key exists but value is null
    /// - Key exists but value is not assignable to type T
    /// </returns>
    /// <remarks>
    /// This method is safer than Get() + cast because:
    /// - Avoids InvalidCastException if types don't match
    /// - Provides clear null semantics for missing/incompatible data
    /// - Enables command handlers to easily detect type mismatches
    /// 
    /// Example usage in command handlers:
    /// <code>
    /// var sortedSet = dataStore.Get&lt;SortedSet&gt;("myzset");
    /// if (sortedSet == null) {
    ///     // Either key doesn't exist OR it's not a SortedSet
    ///     // Command handler can return appropriate error
    /// }
    /// </code>
    /// </remarks>
    public T? Get<T>(string key) where T : class
    {
        lock (_lock)
        {
            return _store.TryGetValue(key, out var value) && value is T typedValue 
                ? typedValue 
                : null;
        }
    }

    /// <summary>
    /// Stores a value for the specified key, creating or overwriting as needed.
    /// This is the primary method for persisting data in the Redis store.
    /// </summary>
    /// <param name="key">The Redis key (string identifier)</param>
    /// <param name="value">
    /// The value to store. Can be:
    /// - string (for simple key-value storage)
    /// - SortedSet (for sorted set operations)
    /// - null (to explicitly store a null value)
    /// - Future: Lists, Hashes, Sets, etc.
    /// </param>
    /// <remarks>
    /// Behavior:
    /// - Creates the key if it doesn't exist
    /// - Overwrites the existing value if the key exists (regardless of type)
    /// - Does NOT automatically remove expiration times (caller's responsibility)
    /// 
    /// Type Overwriting:
    /// Redis allows changing the data type of a key:
    /// - SET mykey "hello" (creates string)
    /// - ZADD mykey 1.0 "member" (overwrites with sorted set)
    /// This is standard Redis behavior and fully supported.
    /// 
    /// Thread Safety: Atomic operation under lock ensures consistency.
    /// Performance: O(1) average case for Dictionary operations.
    /// </remarks>
    public void Set(string key, object? value)
    {
        lock (_lock)
        {
            _store[key] = value;
        }
    }

    /// <summary>
    /// Removes a key and its associated value from the data store.
    /// Used by DEL commands and background expiration cleanup.
    /// </summary>
    /// <param name="key">The Redis key to remove</param>
    /// <returns>
    /// True if the key existed and was successfully removed.
    /// False if the key didn't exist (no-op).
    /// </returns>
    /// <remarks>
    /// Important: This method only removes data from the store.
    /// Callers should also remove expiration tracking:
    /// <code>
    /// bool removed = dataStore.Remove(key);
    /// if (removed) {
    ///     expirationService.RemoveExpiration(key);
    /// }
    /// </code>
    /// 
    /// Used by:
    /// - DEL command (manual key deletion)
    /// - Background expiration process (automatic cleanup)
    /// - Type conversion operations (implicit removal)
    /// 
    /// Thread Safety: Atomic operation ensures no partial removals.
    /// Performance: O(1) average case for Dictionary.Remove().
    /// </remarks>
    public bool Remove(string key)
    {
        lock (_lock)
        {
            return _store.Remove(key);
        }
    }

    /// <summary>
    /// Checks whether a key exists in the data store without retrieving its value.
    /// Optimized for existence testing when the value itself is not needed.
    /// </summary>
    /// <param name="key">The Redis key to check for existence</param>
    /// <returns>
    /// True if the key exists in the store (regardless of its value).
    /// False if the key doesn't exist.
    /// </returns>
    /// <remarks>
    /// Performance Benefits:
    /// - O(1) operation using Dictionary.ContainsKey()
    /// - Doesn't retrieve or deserialize the value
    /// - More efficient than Get() when only existence matters
    /// 
    /// Important Note:
    /// This method only checks DataStore existence, not expiration status.
    /// Command handlers should also check expiration:
    /// <code>
    /// if (!dataStore.Exists(key)) {
    ///     return KeyNotExists;
    /// }
    /// if (expirationService.IsExpired(key)) {
    ///     dataStore.Remove(key);
    ///     return KeyNotExists; // Lazy expiration
    /// }
    /// </code>
    /// 
    /// Used by: EXISTS command, TTL command, conditional operations.
    /// </remarks>
    public bool Exists(string key)
    {
        lock (_lock)
        {
            return _store.ContainsKey(key);
        }
    }

    /// <summary>
    /// Returns all keys currently stored in the data store.
    /// Used for operations that need to iterate over all keys.
    /// </summary>
    /// <returns>
    /// A collection containing all key names in the store.
    /// The collection is a snapshot (copy) to prevent concurrent modification issues.
    /// </returns>
    /// <remarks>
    /// Performance Warning:
    /// - O(n) operation where n is the number of keys
    /// - Creates a copy of all keys to ensure thread safety
    /// - Can be memory-intensive with large numbers of keys
    /// - Should be used sparingly in production environments
    /// 
    /// Primary Uses:
    /// - KEYS command (list all keys matching pattern)
    /// - Background maintenance operations
    /// - Debugging and monitoring tools
    /// - Database backup/export operations
    /// 
    /// Thread Safety:
    /// Returns a snapshot (ToList()) to avoid ConcurrentModificationException
    /// if the original dictionary is modified while iterating.
    /// 
    /// Note: May include keys that have expired but haven't been cleaned up yet.
    /// Callers should check expiration status if needed.
    /// </remarks>
    public IEnumerable<string> GetAllKeys()
    {
        lock (_lock)
        {
            return _store.Keys.ToList(); // Return a copy to avoid concurrent modification
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
    /// - The count reflects the current in-memory state
    /// - Used for monitoring server memory usage and key distribution
    /// 
    /// Typical Uses:
    /// - DBSIZE command (Redis compatibility)
    /// - Server monitoring and alerting
    /// - Capacity planning and resource management
    /// - Performance benchmarking
    /// 
    /// Thread Safety: Atomic read of Dictionary.Count under lock.
    /// </remarks>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _store.Count;
            }
        }
    }
}
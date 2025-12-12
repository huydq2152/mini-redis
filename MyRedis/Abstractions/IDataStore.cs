namespace MyRedis.Abstractions;

/// <summary>
/// Abstraction for data storage operations - the core key-value store.
///
/// This is the heart of the Redis server where all data is stored and retrieved.
/// It acts as a polymorphic key-value dictionary that can store any type of value.
///
/// Supported Value Types:
/// - Strings (simple key-value pairs)
/// - SortedSet (for ZADD/ZRANGE commands)
/// - Future: Lists, Hashes, Sets, etc.
///
/// Implementation: InMemoryDataStore
/// - Uses a Dictionary<string, object?> for storage
/// - Thread-safe via lock-based synchronization
/// - All operations are O(1) except GetAllKeys() which is O(n)
///
/// Thread Safety:
/// The implementation must be thread-safe because:
/// - Background tasks delete expired keys
/// - Client commands read/write data
/// - Multiple operations may happen "simultaneously" in the event loop
///
/// Current implementation uses a simple lock for all operations.
/// For high concurrency, this could be replaced with ConcurrentDictionary
/// or reader-writer locks.
/// </summary>
public interface IDataStore
{
    /// <summary>
    /// Gets a value by key without type checking.
    ///
    /// Use this when you don't know the type of the value in advance,
    /// or when you need to check the type yourself.
    ///
    /// Returns null if:
    /// - The key doesn't exist
    /// - The value is explicitly stored as null
    /// </summary>
    /// <param name="key">The key to retrieve</param>
    /// <returns>The value if found, null otherwise</returns>
    object? Get(string key);

    /// <summary>
    /// Gets a value by key with type checking and casting.
    ///
    /// This is safer than Get(key) followed by a cast because:
    /// 1. Returns null if the key doesn't exist (instead of throwing)
    /// 2. Returns null if the value exists but is not type T
    /// 3. Avoids InvalidCastException
    ///
    /// Example usage:
    /// <code>
    /// var sortedSet = dataStore.Get&lt;SortedSet&gt;("myzset");
    /// if (sortedSet == null) {
    ///     // Key doesn't exist OR value is not a SortedSet
    ///     // Handle error (e.g., WRONGTYPE)
    /// }
    /// </code>
    /// </summary>
    /// <typeparam name="T">Expected type (must be a reference type)</typeparam>
    /// <param name="key">The key to retrieve</param>
    /// <returns>The typed value if found and type matches, null otherwise</returns>
    T? Get<T>(string key) where T : class;

    /// <summary>
    /// Sets a value for the given key, overwriting any existing value.
    ///
    /// DEPRECATED: Use SetWithType() instead for explicit type tracking.
    ///
    /// This operation:
    /// - Creates the key if it doesn't exist
    /// - Overwrites the existing value if the key exists
    /// - Defaults to RedisType.String (may be incorrect!)
    /// - Preserves existing expiration if key already exists
    ///
    /// The value can be any object:
    /// - string (for simple key-value storage)
    /// - SortedSet (for sorted set commands)
    /// - null (to explicitly store a null value)
    /// </summary>
    /// <param name="key">The key</param>
    /// <param name="value">The value to store (any object or null)</param>
    void Set(string key, object? value);

    /// <summary>
    /// Sets a value with explicit type and optional expiration (RECOMMENDED).
    ///
    /// This is the preferred method for command handlers to ensure:
    /// - Correct type tracking (enables WRONGTYPE error detection)
    /// - Explicit expiration management
    /// - No ambiguity about data types
    ///
    /// Usage Examples:
    /// ```csharp
    /// // SET command
    /// dataStore.SetWithType(key, stringValue, RedisType.String, expireAt: -1);
    ///
    /// // SET command with expiration
    /// long expireAt = Environment.TickCount64 + (ttl * 1000);
    /// dataStore.SetWithType(key, value, RedisType.String, expireAt);
    ///
    /// // ZADD command
    /// dataStore.SetWithType(key, sortedSet, RedisType.SortedSet, expireAt: -1);
    /// ```
    /// </summary>
    /// <param name="key">The Redis key</param>
    /// <param name="value">The value to store</param>
    /// <param name="type">The Redis data type (String, SortedSet, etc.)</param>
    /// <param name="expireAt">Absolute expiration timestamp (-1 for no expiration)</param>
    void SetWithType(string key, object? value, Storage.RedisType type, long expireAt = -1);

    /// <summary>
    /// Removes a key and its value from the store.
    ///
    /// Used by:
    /// - DEL command
    /// - Background expiration (deleting expired keys)
    ///
    /// Note: This only removes the data, not the expiration entry.
    /// Caller should also call ExpirationService.RemoveExpiration() if needed.
    /// </summary>
    /// <param name="key">The key to remove</param>
    /// <returns>True if the key existed and was removed, false if it didn't exist</returns>
    bool Remove(string key);

    /// <summary>
    /// Checks if a key exists in the store.
    ///
    /// This is O(1) and doesn't retrieve the value.
    /// Use this when you only need to know if a key exists, not its value.
    ///
    /// Note: This doesn't check expiration. A key may exist but be expired.
    /// For expired keys, use ExpirationService.IsExpired() as well.
    /// </summary>
    /// <param name="key">The key to check</param>
    /// <returns>True if the key exists, false otherwise</returns>
    bool Exists(string key);

    /// <summary>
    /// Gets all keys currently in the store.
    ///
    /// Used by:
    /// - KEYS command (list all keys)
    /// - Background tasks that need to iterate over all keys
    ///
    /// Performance: O(n) where n is the number of keys
    /// The returned collection is a snapshot (copy) to avoid concurrent modification.
    ///
    /// Note: May include expired keys. Caller should check expiration if needed.
    /// </summary>
    /// <returns>Collection of all keys in the store</returns>
    IEnumerable<string> GetAllKeys();

    /// <summary>
    /// Gets the total number of keys in the store.
    ///
    /// This is O(1) in most implementations (Dictionary.Count).
    ///
    /// Note: May include expired keys that haven't been cleaned up yet.
    /// </summary>
    int Count { get; }
}
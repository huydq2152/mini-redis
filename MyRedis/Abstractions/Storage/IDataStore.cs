using MyRedis.Storage.Models;

namespace MyRedis.Abstractions.Storage;

/// <summary>
/// Abstraction for data storage operations - the core key-value store.
///
/// This is the heart of the Redis server where all data is stored and retrieved.
/// It acts as a polymorphic key-value dictionary that can store any type of value.
/// </summary>
public interface IDataStore
{
    /// <summary>
    /// Gets a value by key without type checking.
    ///
    /// Use this when you don't know the type of the value in advance,
    /// or when you need to check the type yourself.
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
    /// 3. Avoids InvalidCastException at runtime
    /// </summary>
    /// <typeparam name="T">Expected type (must be a reference type)</typeparam>
    /// <param name="key">The key to retrieve</param>
    /// <returns>The typed value if found and type matches, null otherwise</returns>
    T? Get<T>(string key) where T : class;

    /// <summary>
    /// Sets a value with explicit type and optional expiration.
    ///
    /// This is the preferred method for command handlers to ensure:
    /// - Correct type tracking (enables WRONGTYPE error detection)
    /// - Explicit expiration management
    /// - No ambiguity about data types stored
    /// </summary>
    /// <param name="key">The Redis key</param>
    /// <param name="value">The value to store</param>
    /// <param name="type">The Redis data type (String, SortedSet, etc.)</param>
    /// <param name="expireAt">Absolute expiration timestamp (-1 for no expiration)</param>
    void SetWithType(string key, object? value, RedisType type, long expireAt = -1);

    /// <summary>
    /// Removes a key and its associated RedisEntry from the store.
    ///
    /// With the unified RedisEntry architecture, this single operation removes:
    /// - The key itself
    /// - The value (freed for GC)
    /// - Expiration metadata (no separate cleanup needed)
    /// - Type information
    ///
    /// No coordination with ExpirationService needed - expiration is part of the entry.
    /// </summary>
    /// <param name="key">The key to remove</param>
    /// <returns>True if the key existed and was removed, false if it didn't exist</returns>
    bool Remove(string key);

    /// <summary>
    /// Checks if a key exists in the store and is not expired.
    ///
    /// This is O(1) and doesn't retrieve the value.
    /// Use this when you only need to know if a key exists, not its value.
    ///
    /// Atomic Expiration Handling:
    /// The implementation automatically checks expiration as part of the existence check.
    /// If the key exists but has expired, it's immediately deleted (lazy expiration).
    /// This ensures Exists() never returns true for an expired key.
    ///
    /// No separate ExpirationService check needed - expiration is handled atomically.
    /// </summary>
    /// <param name="key">The key to check</param>
    /// <returns>True if the key exists and is not expired, false otherwise</returns>
    bool Exists(string key);

    /// <summary>
    /// Gets all keys currently in the store.
    ///
    /// Used by:
    /// - KEYS command (list all keys)
    /// - Background tasks that need to iterate over all keys
    ///
    /// Performance: O(n) where n is the number of keys, expensive with large datasets
    /// The returned collection is a snapshot (copy) for safe iteration.
    ///
    /// Expiration Handling:
    /// - Returns ALL keys, including expired ones (lazy expiration not applied)
    /// - Lazy expiration only happens on direct access (Get/Exists)
    /// - Active expiration cleanup runs in background
    /// - If you need non-expired keys only, filter with entry.IsExpired() check
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
namespace MyRedis.Abstractions;

/// <summary>
/// Abstraction for data storage operations
/// </summary>
public interface IDataStore
{
    /// <summary>
    /// Gets a value by key
    /// </summary>
    /// <param name="key">The key to retrieve</param>
    /// <returns>The value if found, null otherwise</returns>
    object? Get(string key);

    /// <summary>
    /// Gets a value by key with type checking
    /// </summary>
    /// <typeparam name="T">Expected type</typeparam>
    /// <param name="key">The key to retrieve</param>
    /// <returns>The typed value if found and type matches, default(T) otherwise</returns>
    T? Get<T>(string key) where T : class;

    /// <summary>
    /// Sets a value for the given key
    /// </summary>
    /// <param name="key">The key</param>
    /// <param name="value">The value to store</param>
    void Set(string key, object? value);

    /// <summary>
    /// Removes a key from the store
    /// </summary>
    /// <param name="key">The key to remove</param>
    /// <returns>True if the key existed and was removed, false otherwise</returns>
    bool Remove(string key);

    /// <summary>
    /// Checks if a key exists in the store
    /// </summary>
    /// <param name="key">The key to check</param>
    /// <returns>True if the key exists, false otherwise</returns>
    bool Exists(string key);

    /// <summary>
    /// Gets all keys in the store
    /// </summary>
    /// <returns>Collection of all keys</returns>
    IEnumerable<string> GetAllKeys();

    /// <summary>
    /// Gets the count of items in the store
    /// </summary>
    int Count { get; }
}
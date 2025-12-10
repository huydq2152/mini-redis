namespace MyRedis.Abstractions;

/// <summary>
/// Service for managing key expiration
/// </summary>
public interface IExpirationService
{
    /// <summary>
    /// Sets expiration time for a key
    /// </summary>
    /// <param name="key">The key to set expiration for</param>
    /// <param name="timeoutMs">Timeout in milliseconds</param>
    void SetExpiration(string key, int timeoutMs);

    /// <summary>
    /// Removes expiration for a key
    /// </summary>
    /// <param name="key">The key to remove expiration for</param>
    void RemoveExpiration(string key);

    /// <summary>
    /// Checks if a key is expired
    /// </summary>
    /// <param name="key">The key to check</param>
    /// <returns>True if the key is expired, false otherwise</returns>
    bool IsExpired(string key);

    /// <summary>
    /// Gets the TTL for a key
    /// </summary>
    /// <param name="key">The key to get TTL for</param>
    /// <returns>TTL in milliseconds, null if no expiration is set</returns>
    long? GetTtl(string key);

    /// <summary>
    /// Gets the timeout for the next expiration event
    /// </summary>
    /// <returns>Timeout in milliseconds until next expiration</returns>
    int GetNextTimeout();

    /// <summary>
    /// Processes expired keys and returns them
    /// </summary>
    /// <returns>List of expired keys</returns>
    IList<string> ProcessExpiredKeys();
}
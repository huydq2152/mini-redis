namespace MyRedis.Abstractions.Storage;

/// <summary>
/// Service for managing key expiration and Time-To-Live (TTL).
/// </summary>
public interface IExpirationService
{
    /// <summary>
    /// Sets or updates the expiration time for a key.
    /// </summary>
    /// <param name="key">The key to set expiration for</param>
    /// <param name="timeoutMs">Time to live in milliseconds from now</param>
    void SetExpiration(string key, int timeoutMs);

    /// <summary>
    /// Removes expiration for a key, making it persistent.
    /// </summary>
    /// <param name="key">The key to remove expiration for</param>
    void RemoveExpiration(string key);

    /// <summary>
    /// Checks if a key has expired (passive expiration check).
    /// </summary>
    /// <param name="key">The key to check</param>
    /// <returns>True if the key has expiration set AND the time has passed, false otherwise</returns>
    bool IsExpired(string key);

    /// <summary>
    /// Gets the remaining time-to-live for a key.
    /// </summary>
    /// <param name="key">The key to get TTL for</param>
    /// <returns>TTL in milliseconds, null if no expiration is set</returns>
    long? GetTimeToLive(string key);

    /// <summary>
    /// Gets the time in milliseconds until the next key expires.
    /// </summary>
    /// <returns>Milliseconds until next expiration, or default timeout</returns>
    int GetNextTimeout();

    /// <summary>
    /// Processes and returns all keys that have expired (active expiration).
    /// </summary>
    /// <returns>List of keys that have expired (perhaps empty)</returns>
    IList<string> ProcessExpiredKeys();
}
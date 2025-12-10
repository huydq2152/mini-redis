namespace MyRedis.Abstractions;

/// <summary>
/// Service for managing key expiration and Time-To-Live (TTL).
///
/// Redis supports automatic expiration of keys after a specified time,
/// which is essential for caching, session management, and temporary data.
///
/// Implementation Strategy (Min-Heap):
/// - Keys with expiration are stored in a min-heap ordered by expiration time
/// - The heap root always contains the next key to expire
/// - O(log n) for SetExpiration and ProcessExpiredKeys
/// - O(1) for GetNextTimeout (just peek at heap root)
///
/// How Expiration Works:
/// 1. EXPIRE command calls SetExpiration(key, timeMs)
/// 2. Key and expiration time are added to the heap
/// 3. BackgroundTaskManager periodically calls ProcessExpiredKeys()
/// 4. Expired keys are returned and deleted from the data store
/// 5. GetNextTimeout() tells the event loop when to check again
///
/// Passive vs. Active Expiration:
/// - Passive: Keys are checked for expiration when accessed (GET, etc.)
/// - Active: Background task periodically scans for expired keys
/// This implementation uses BOTH strategies.
///
/// Thread Safety:
/// Must be thread-safe because expiration operations happen from:
/// - Client commands (EXPIRE, GET with expiration check)
/// - Background tasks (periodic expiration cleanup)
/// </summary>
public interface IExpirationService
{
    /// <summary>
    /// Sets or updates the expiration time for a key.
    ///
    /// Used by the EXPIRE command to set a TTL on a key.
    ///
    /// Operation:
    /// 1. Calculate absolute expiration time (current time + timeout)
    /// 2. Add or update the key in the min-heap
    /// 3. Heap is reorganized to maintain min-heap property (next to expire at root)
    ///
    /// If the key already has an expiration, it's updated to the new value.
    /// The key continues to exist in the data store; only its expiration is tracked.
    /// </summary>
    /// <param name="key">The key to set expiration for</param>
    /// <param name="timeoutMs">Time to live in milliseconds from now</param>
    void SetExpiration(string key, int timeoutMs);

    /// <summary>
    /// Removes expiration for a key, making it persistent.
    ///
    /// Used when:
    /// - A key is deleted (DEL command)
    /// - A key is set without expiration (SET command)
    /// - PERSIST command (if implemented)
    ///
    /// After calling this, the key will never expire automatically.
    /// If the key had no expiration, this is a no-op.
    /// </summary>
    /// <param name="key">The key to remove expiration for</param>
    void RemoveExpiration(string key);

    /// <summary>
    /// Checks if a key has expired (passive expiration check).
    ///
    /// This implements "passive expiration" - checking expiration when a key is accessed.
    /// Called by GET, EXISTS, and other commands before returning data.
    ///
    /// Performance: O(1) - just compares expiration time to current time
    ///
    /// Example usage in GET command:
    /// <code>
    /// if (expirationService.IsExpired(key)) {
    ///     dataStore.Remove(key);
    ///     expirationService.RemoveExpiration(key);
    ///     return Nil;
    /// }
    /// </code>
    /// </summary>
    /// <param name="key">The key to check</param>
    /// <returns>True if the key has expiration set AND the time has passed, false otherwise</returns>
    bool IsExpired(string key);

    /// <summary>
    /// Gets the remaining time-to-live for a key.
    ///
    /// Used by the TTL command to show how much time is left before expiration.
    ///
    /// Returns:
    /// - Positive number: Milliseconds remaining until expiration
    /// - null: Key has no expiration (it's persistent)
    /// - May return 0 or negative if key just expired but hasn't been cleaned up yet
    ///
    /// Note: This doesn't check if the key exists in the data store,
    /// only if it has an expiration time set.
    /// </summary>
    /// <param name="key">The key to get TTL for</param>
    /// <returns>TTL in milliseconds, null if no expiration is set</returns>
    long? GetTtl(string key);

    /// <summary>
    /// Gets the time in milliseconds until the next key expires.
    ///
    /// This is used by the event loop to determine how long to sleep in Socket.Select().
    /// By sleeping for exactly this amount of time, the loop wakes up just when
    /// a key needs to be expired.
    ///
    /// Implementation:
    /// 1. Peek at the min-heap root (earliest expiration)
    /// 2. Calculate time difference from now
    /// 3. Return the difference (may be 0 if already expired)
    ///
    /// If no keys have expiration, returns a default timeout (e.g., 1000ms).
    /// </summary>
    /// <returns>Milliseconds until next expiration, or default timeout</returns>
    int GetNextTimeout();

    /// <summary>
    /// Processes and returns all keys that have expired (active expiration).
    ///
    /// This implements "active expiration" - proactive cleanup of expired keys.
    /// Called by BackgroundTaskManager on each event loop iteration.
    ///
    /// Algorithm:
    /// 1. Check the min-heap root
    /// 2. If expired, pop it and add to result list
    /// 3. Repeat until root is not expired
    /// 4. Return all expired keys
    ///
    /// The returned keys should be deleted from the data store by the caller.
    /// This method only removes them from the expiration heap.
    ///
    /// Performance: O(k log n) where k is the number of expired keys.
    /// Typically k is small (0-5), so this is very fast.
    /// </summary>
    /// <returns>List of keys that have expired (may be empty)</returns>
    IList<string> ProcessExpiredKeys();
}
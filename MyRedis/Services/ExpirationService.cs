using MyRedis.Abstractions;
using MyRedis.Core;

namespace MyRedis.Services;

/// <summary>
/// Service adapter that provides Redis key expiration and Time-To-Live (TTL) management
/// by wrapping the existing ExpirationManager implementation.
///
/// Design Pattern: Adapter Pattern
/// This class adapts the concrete ExpirationManager to the IExpirationService interface,
/// enabling dependency injection and providing a clean abstraction for TTL operations.
/// This separation allows for easier testing and potential future implementation changes.
///
/// Redis Expiration Strategy:
/// Redis uses both active and passive expiration strategies for optimal performance:
/// - Passive: Keys are checked for expiration when accessed (GET, EXISTS, etc.)
/// - Active: Background process periodically scans and removes expired keys
///
/// Implementation Strategy (Min-Heap Priority Queue):
/// The underlying ExpirationManager uses a min-heap data structure where:
/// - Keys are ordered by their absolute expiration time
/// - The heap root always contains the next key to expire
/// - O(log n) complexity for SetExpiration and ProcessExpiredKeys
/// - O(1) complexity for GetNextTimeout (peek at root)
///
/// Expiration Workflow:
/// 1. Client issues EXPIRE command → SetExpiration() adds key to heap
/// 2. Background task periodically calls ProcessExpiredKeys() → returns expired keys
/// 3. Expired keys are removed from DataStore by background task
/// 4. Client commands call IsExpired() for lazy expiration checking
/// 5. GetNextTimeout() optimizes event loop timing for efficient cleanup
///
/// Thread Safety:
/// All operations are thread-safe as expiration management happens from:
/// - Client command threads (EXPIRE, GET with expiration checks)
/// - Background cleanup thread (periodic expiration processing)
/// </summary>
public class ExpirationService : IExpirationService
{
    /// <summary>
    /// The underlying ExpirationManager that provides the actual expiration functionality.
    /// This adapter delegates all TTL operations to this instance while providing
    /// interface-based abstraction for dependency injection and testing.
    /// </summary>
    private readonly ExpirationManager _expirationManager;

    /// <summary>
    /// Initializes a new instance of the ExpirationService with the required ExpirationManager.
    /// The ExpirationManager must be properly configured and thread-safe.
    /// </summary>
    /// <param name="expirationManager">The ExpirationManager instance to wrap and adapt</param>
    /// <exception cref="ArgumentNullException">Thrown when expirationManager is null</exception>
    public ExpirationService(ExpirationManager expirationManager)
    {
        _expirationManager = expirationManager ?? throw new ArgumentNullException(nameof(expirationManager));
    }

    /// <summary>
    /// Sets or updates the expiration time for a Redis key.
    /// Used by the EXPIRE command to establish Time-To-Live for keys.
    /// The key will be automatically deleted after the specified timeout.
    /// </summary>
    /// <param name="key">The Redis key to set expiration for</param>
    /// <param name="timeoutMs">Timeout in milliseconds from now (relative time)</param>
    /// <remarks>
    /// Operation details:
    /// 1. Calculates absolute expiration time (current time + timeout)
    /// 2. Adds or updates the key in the min-heap priority queue
    /// 3. Heap maintains ordering by expiration time for efficient processing
    /// 
    /// If the key already has an expiration, it's updated to the new value.
    /// The key continues to exist in the DataStore until expiration occurs.
    /// Thread-safe for concurrent access from multiple command handlers.
    /// </remarks>
    public void SetExpiration(string key, int timeoutMs)
    {
        _expirationManager.SetExpiration(key, timeoutMs);
    }

    /// <summary>
    /// Removes expiration tracking for a key, making it persistent (no automatic deletion).
    /// Used when keys are deleted (DEL) or when expiration should be cleared (PERSIST).
    /// </summary>
    /// <param name="key">The Redis key to remove expiration tracking for</param>
    /// <remarks>
    /// Common usage scenarios:
    /// - DEL command: Remove expiration when manually deleting a key
    /// - SET command: Clear expiration when overwriting a key value
    /// - PERSIST command: Make a key permanent by removing its TTL
    /// 
    /// Safe to call even if the key has no expiration set (no-op).
    /// After calling this, the key will never expire automatically.
    /// Thread-safe for concurrent access with other expiration operations.
    /// </remarks>
    public void RemoveExpiration(string key)
    {
        _expirationManager.RemoveExpiration(key);
    }

    /// <summary>
    /// Checks if a key has expired, implementing passive expiration detection.
    /// This method enables "lazy expiration" where keys are checked for expiration
    /// when accessed rather than waiting for background cleanup.
    /// </summary>
    /// <param name="key">The Redis key to check for expiration</param>
    /// <returns>
    /// True if the key has an expiration set AND the expiration time has passed.
    /// False if the key has no expiration or the expiration time has not yet been reached.
    /// </returns>
    /// <remarks>
    /// Used by command handlers (GET, EXISTS, etc.) to implement lazy expiration:
    /// <code>
    /// if (expirationService.IsExpired(key)) {
    ///     dataStore.Remove(key);
    ///     expirationService.RemoveExpiration(key);
    ///     return Nil; // Key expired, treat as non-existent
    /// }
    /// </code>
    /// 
    /// Performance: O(1) - simple timestamp comparison
    /// Critical for maintaining data consistency and Redis-compliant behavior.
    /// </remarks>
    public bool IsExpired(string key)
    {
        return _expirationManager.IsExpired(key);
    }

    /// <summary>
    /// Gets the remaining Time-To-Live for a key in milliseconds.
    /// Used by the TTL command to report how much time remains before key expiration.
    /// </summary>
    /// <param name="key">The Redis key to get TTL information for</param>
    /// <returns>
    /// - Positive number: Milliseconds remaining until expiration
    /// - null: Key has no expiration set (persistent key)
    /// - May return 0 or negative if key just expired but hasn't been cleaned up yet
    /// </returns>
    /// <remarks>
    /// Note: This method only checks expiration tracking, not DataStore key existence.
    /// The TTL command handler should verify key existence separately.
    /// 
    /// TTL command return value mapping:
    /// - null → -1 (key exists but has no expiration)
    /// - positive value → value/1000 (convert to seconds for Redis compatibility)
    /// - Used with DataStore.Exists() check → -2 (key doesn't exist)
    /// </remarks>
    public long? GetTtl(string key)
    {
        return _expirationManager.GetTTL(key);
    }

    /// <summary>
    /// Calculates the time in milliseconds until the next key expires.
    /// Used by the event loop to optimize timing for active expiration processing.
    /// </summary>
    /// <returns>
    /// Milliseconds until the next key expiration, or a default timeout if no keys have expiration.
    /// </returns>
    /// <remarks>
    /// Implementation details:
    /// 1. Peeks at the min-heap root (earliest expiration time)
    /// 2. Calculates time difference from current time
    /// 3. Returns the difference (may be 0 if keys are already expired)
    /// 
    /// Used by BackgroundTaskManager to optimize event loop timing:
    /// - Avoids constant polling by sleeping until expiration processing is needed
    /// - Ensures timely cleanup of expired keys
    /// - Reduces CPU usage when no keys have expiration
    /// </remarks>
    public int GetNextTimeout()
    {
        return _expirationManager.GetNextTimeout();
    }

    /// <summary>
    /// Processes and returns all keys that have expired, implementing active expiration.
    /// Called by background tasks to proactively clean up expired keys without
    /// waiting for client access (passive expiration).
    /// </summary>
    /// <returns>
    /// List of keys that have expired and should be deleted from the DataStore.
    /// The list may be empty if no keys have expired since the last check.
    /// </returns>
    /// <remarks>
    /// Active expiration algorithm:
    /// 1. Check the min-heap root for the earliest expiration
    /// 2. If expired, remove from heap and add to result list
    /// 3. Repeat until the root is not expired (or heap is empty)
    /// 4. Return all expired keys for DataStore cleanup
    /// 
    /// Performance: O(k log n) where k is the number of expired keys
    /// Typically k is small (0-10 keys per iteration) making this very efficient.
    /// 
    /// The caller (BackgroundTaskManager) is responsible for:
    /// - Removing returned keys from the DataStore
    /// - Calling this method periodically for timely cleanup
    /// </remarks>
    public IList<string> ProcessExpiredKeys()
    {
        return _expirationManager.ProcessExpiredKeys();
    }
}
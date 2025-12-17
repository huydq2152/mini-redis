using MyRedis.Abstractions.Storage;
using MyRedis.Core.Storage;

namespace MyRedis.Services.Storage;

/// <summary>
/// Service adapter that provides Redis key expiration and Time-To-Live (TTL) management
/// by wrapping the existing ExpirationManager implementation.
///
/// Supports automatic expiration of keys after a specified time,
/// which is essential for caching, session management, and temporary data.
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
    public long? GetTimeToLive(string key)
    {
        return _expirationManager.GetTimeToLive(key);
    }

    /// <summary>
    /// Calculates the time in milliseconds until the next key expires.
    /// Used by the event loop to optimize timing for active expiration processing.
    /// </summary>
    /// <returns>
    /// Milliseconds until the next key expiration, or a default timeout if no keys have expiration.
    /// </returns>
    /// <remarks>
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
    /// The caller (BackgroundTaskManager) is responsible for:
    /// - Removing returned keys from the DataStore
    /// - Calling this method periodically for timely cleanup
    /// </remarks>
    public IList<string> ProcessExpiredKeys()
    {
        return _expirationManager.ProcessExpiredKeys();
    }
}
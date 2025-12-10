namespace MyRedis.Core;

/// <summary>
/// Manages key expiration using a min-heap priority queue.
///
/// Data Structure Choice: Min-Heap
/// - The key with the earliest expiration time is always at the root
/// - O(log n) insertion and removal
/// - O(1) peek at next expiration
/// - Perfect for time-based priority processing
///
/// Why Two Data Structures?
/// 1. PriorityQueue (Min-Heap): Ordered by expiration time for efficient processing
/// 2. Dictionary: O(1) lookup to check if a key has expiration
///
/// Lazy Update Strategy:
/// - When updating a key's TTL, we don't remove the old entry from the heap
/// - Instead, we add a new entry with the updated time
/// - When processing, we check the dictionary to see if the entry is still valid
/// - This avoids the O(n) cost of searching and removing from the middle of the heap
/// - Old entries become "garbage" and are skipped during processing
///
/// Thread Safety: Not thread-safe (relies on single-threaded event loop).
/// For multi-threaded scenarios, add lock protection.
/// </summary>
public class ExpirationManager
{
    // Min-heap ordered by expiration time (earliest expiration at root)
    // Can contain duplicate keys due to lazy update strategy
    private readonly PriorityQueue<string, long> _ttlQueue = new PriorityQueue<string, long>();

    // Authoritative source of truth for key expiration times
    // Used to validate entries from the heap (filtering out stale duplicates)
    private readonly Dictionary<string, long> _keyExpirations = new Dictionary<string, long>();

    /// <summary>
    /// Sets or updates the expiration time for a key.
    ///
    /// Algorithm:
    /// 1. Calculate absolute expiration time (current time + duration)
    /// 2. Update the dictionary with the new expiration time
    /// 3. Add entry to the min-heap (even if key already exists)
    ///
    /// Lazy Update Strategy:
    /// - If the key already has an expiration, we DON'T remove the old heap entry
    /// - We simply add a new entry with the updated time
    /// - The old entry becomes "stale garbage" in the heap
    /// - During processing, we validate against the dictionary and skip stale entries
    /// - This avoids O(n) heap search/removal, keeping insertion at O(log n)
    ///
    /// Example:
    /// - EXPIRE mykey 10 -> Heap: [(mykey, T+10)]
    /// - EXPIRE mykey 20 -> Heap: [(mykey, T+10), (mykey, T+20)], Dict: {mykey: T+20}
    /// - When T+10 is processed, we check Dict and see it doesn't match, so skip it
    ///
    /// Performance: O(log n) for heap insertion
    /// </summary>
    public void SetExpiration(string key, long durationMs)
    {
        // Calculate when this key should expire (absolute timestamp)
        long expireAt = GetNow() + durationMs;

        // Update the authoritative expiration time in the dictionary
        // This overwrites any previous expiration for this key
        _keyExpirations[key] = expireAt;

        // Add the entry to the min-heap (ordered by expiration time)
        // Note: We don't remove old entries - this is intentional (lazy update)
        // Old entries will be filtered out during ProcessExpiredKeys()
        _ttlQueue.Enqueue(key, expireAt);
    }

    /// <summary>
    /// Checks if a key has expired (passive expiration).
    ///
    /// This implements "lazy expiration" - checking when a key is accessed
    /// rather than proactively scanning all keys.
    ///
    /// Called by:
    /// - GET command (before returning a value)
    /// - EXISTS command
    /// - Any command that needs to check if a key is valid
    ///
    /// Behavior:
    /// - If key has no expiration: returns false (key is persistent)
    /// - If key has expiration but time hasn't passed: returns false
    /// - If key has expired: removes expiration metadata and returns true
    ///
    /// Note: This only removes the expiration metadata, not the key itself.
    /// The caller is responsible for deleting the key from the data store.
    ///
    /// Performance: O(1) - just a dictionary lookup and time comparison
    /// </summary>
    public bool IsExpired(string key)
    {
        // Check if this key has an expiration time set
        if (!_keyExpirations.TryGetValue(key, out long expireAt))
            return false; // No expiration = never expires

        // Check if the current time has passed the expiration time
        if (GetNow() > expireAt)
        {
            // Key has expired - clean up the expiration metadata
            // (The actual key data will be removed by the caller)
            _keyExpirations.Remove(key);
            return true;
        }

        // Key has expiration but hasn't expired yet
        return false;
    }

    /// <summary>
    /// Gets the remaining time-to-live for a key in milliseconds.
    ///
    /// Used by the TTL command to show how long until a key expires.
    ///
    /// Return values:
    /// - null: Key has no expiration (it's persistent)
    /// - Positive number: Milliseconds remaining until expiration
    /// - 0: Key has just expired (but may not be cleaned up yet)
    ///
    /// Note: This doesn't check if the key exists in the data store,
    /// only if it has an expiration time set.
    ///
    /// Performance: O(1) - dictionary lookup and arithmetic
    /// </summary>
    public long? GetTTL(string key)
    {
        // Check if the key has an expiration time
        if (!_keyExpirations.TryGetValue(key, out long expireAt))
            return null; // No expiration set

        // Calculate time remaining
        long ttl = expireAt - GetNow();

        // Return at least 0 (negative values mean already expired)
        return ttl > 0 ? ttl : 0;
    }

    /// <summary>
    /// Removes expiration for a key, making it persistent.
    ///
    /// Called when:
    /// - A key is deleted (DEL command)
    /// - A key is set without expiration (SET command)
    /// - PERSIST command (if implemented)
    ///
    /// Lazy Heap Cleanup:
    /// - We only remove from the dictionary, not the heap
    /// - Old heap entries for this key become "garbage"
    /// - They'll be skipped during ProcessExpiredKeys() when the dictionary lookup fails
    /// - This avoids the expensive O(n) heap search operation
    ///
    /// Performance: O(1) - just a dictionary removal
    /// </summary>
    public void RemoveExpiration(string key)
    {
        // Remove from the authoritative dictionary
        _keyExpirations.Remove(key);

        // Note: We don't remove from the heap - it's too expensive (O(n))
        // Old entries will be filtered out during processing (lazy cleanup)
    }

    /// <summary>
    /// Gets the time in milliseconds until the next key expires.
    ///
    /// This is used by the event loop to determine how long to sleep in Socket.Select().
    /// By sleeping for exactly this amount of time, the loop wakes up precisely when
    /// a key needs to be expired, avoiding both busy-waiting and delayed expiration.
    ///
    /// Algorithm:
    /// 1. Peek at the heap root (earliest expiration)
    /// 2. Calculate time difference from now
    /// 3. Return the difference (or 0 if already expired)
    ///
    /// Return values:
    /// - 0: One or more keys have already expired (process immediately)
    /// - Positive number: Milliseconds until next expiration
    /// - 10000 (default): No keys have expiration (sleep for 10 seconds)
    ///
    /// Why 10 seconds default?
    /// - Prevents the event loop from sleeping forever if no keys have expiration
    /// - Allows other background tasks (idle connection cleanup) to run periodically
    /// - Not too frequent (avoids wasted CPU), not too rare (maintains responsiveness)
    ///
    /// Performance: O(1) - just peeks at heap root
    /// </summary>
    public int GetNextTimeout()
    {
        // If no keys have expiration, return default timeout
        if (_ttlQueue.Count == 0)
            return 10000; // 10 seconds

        // Peek at the root of the heap (earliest expiration)
        if (_ttlQueue.TryPeek(out _, out long nextExpire))
        {
            long now = GetNow();

            // If the next expiration is in the past or now, wake up immediately
            if (nextExpire <= now)
                return 0;

            // Return time until next expiration
            return (int)(nextExpire - now);
        }

        // Fallback (should never reach here if Count > 0)
        return 10000;
    }

    /// <summary>
    /// Processes and returns all keys that have expired (active expiration).
    ///
    /// This implements "active expiration" - proactively scanning for expired keys
    /// even if they haven't been accessed. This ensures expired keys don't sit
    /// in memory indefinitely.
    ///
    /// Algorithm:
    /// 1. Peek at the heap root to check the earliest expiration
    /// 2. If not expired yet, stop (everything else expires later)
    /// 3. If expired, dequeue it
    /// 4. Validate against the dictionary (filter out stale duplicates)
    /// 5. If valid and expired, add to result list
    /// 6. Repeat until root is not expired or work limit reached
    ///
    /// Work Limit (Throttling):
    /// - Maximum 100 keys processed per call
    /// - Prevents long-running expiration from blocking the event loop
    /// - If many keys expire at once, they'll be processed across multiple iterations
    /// - This is similar to Redis's incremental expiration strategy
    ///
    /// Filtering Stale Entries:
    /// Due to the lazy update strategy, the heap may contain:
    /// - Duplicate entries for the same key (old TTL values)
    /// - Entries for keys that no longer have expiration
    ///
    /// We validate each entry against the dictionary:
    /// - If key not in dictionary: Skip (expiration was removed)
    /// - If expiration time doesn't match: Skip (this is an old duplicate)
    /// - If expiration time matches and expired: Valid expired key
    ///
    /// Performance: O(k log n) where k = number of entries processed (≤ 100)
    ///
    /// Returns: List of keys that have expired and should be deleted from the data store
    /// </summary>
    public List<string> ProcessExpiredKeys()
    {
        var expiredKeys = new List<string>();
        long now = GetNow();

        // Work limit to prevent blocking the event loop
        // Similar to Redis's ACTIVE_EXPIRE_CYCLE_LOOKUPS_PER_LOOP
        int maxWork = 100;

        // Process up to maxWork expired keys
        while (_ttlQueue.Count > 0 && maxWork > 0)
        {
            // Peek at the root (earliest expiration)
            if (_ttlQueue.TryPeek(out _, out long priority))
            {
                // If root hasn't expired yet, we're done
                // (everything else expires even later)
                if (priority > now)
                    break;
            }

            // Pop the earliest expiration from the heap
            string key = _ttlQueue.Dequeue();

            // Validate against the dictionary (authoritative source of truth)
            if (_keyExpirations.TryGetValue(key, out long actualExpire))
            {
                // Check if this entry is current and expired
                if (actualExpire <= now)
                {
                    // This is a valid expired key
                    expiredKeys.Add(key);
                    _keyExpirations.Remove(key);
                }
                // If actualExpire > now:
                // This is a stale duplicate from a lazy update
                // The key was re-set with a new TTL, so skip this old entry
            }
            // If key not in dictionary:
            // Expiration was removed (e.g., key was deleted or persisted)
            // Skip this stale entry

            maxWork--;
        }

        return expiredKeys;
    }

    /// <summary>
    /// Gets the current time in milliseconds.
    ///
    /// Uses Environment.TickCount64 instead of DateTime for:
    /// - Better performance (no time zone conversions)
    /// - Monotonic clock (doesn't go backwards with system time changes)
    /// - 64-bit prevents overflow (runs for ~292 million years)
    ///
    /// Note: This is relative time (milliseconds since system boot),
    /// not absolute wall-clock time. Perfect for TTL calculations.
    /// </summary>
    private long GetNow() => Environment.TickCount64;
}
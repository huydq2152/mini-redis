using MyRedis.Common.Helpers;

namespace MyRedis.Core.Storage;

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
/// TODO: Zombie Entry Cleanup Strategies and heap can become the bottleneck with a large number of timers (Future Enhancement)
///
/// Current Approach: Lazy cleanup during processing (simple, but accumulates "zombie" entries)
///
/// Alternative Solutions for High-Scale Scenarios:
///
/// Solution A: Garbage Collection Threshold
/// ┌─────────────────────────────────────────────────────────────┐
/// │  Track heap size vs dictionary size ratio                   │
/// │  When heap.Count > dictionary.Count × 2:                    │
/// │  - Rebuild heap from dictionary entries only                │
/// │  - O(n log n) operation but infrequent                      │
/// │                                                             │
/// │  Pros: Automatic cleanup, memory bounded                    │
/// │  Cons: Periodic rebuild cost                                │
/// └─────────────────────────────────────────────────────────────┘
///
/// Solution B: Custom Indexed Heap (Decrease-Key Support)
/// ┌─────────────────────────────────────────────────────────────┐
/// │  Heap + Dictionary<key, heapIndex> for O(log n) updates     │
/// │  - Insert: O(log n)                                         │
/// │  - Update existing: O(log n) decrease/increase-key          │
/// │  - Delete by key: O(log n)                                  │
/// │                                                             │
/// │  On SetExpiration:                                          │
/// │  - If key exists: update priority in-place                  │
/// │  - If key new: insert normally                              │
/// │                                                             │
/// │  Pros: No zombie entries, optimal memory usage              │
/// │  Cons: Complex custom implementation                        │
/// └─────────────────────────────────────────────────────────────┘
///
/// Solution C: Redis-Style Random Sampling (Million+ Keys)
/// ┌─────────────────────────────────────────────────────────────┐
/// │  Replace PriorityQueue with expiration buckets              │
/// │  - Sample 20 random keys every 100ms                        │
/// │  - Check if expired, remove if so                           │
/// │  - Continue if >25% of sample was expired                   │
/// │  - This approach scales to millions of keys without long    │                              
/// │    blocking times                                           │
/// │  - Redis can do this efficiently because it has direct      │  
/// │    access to hash table buckets (C implementation)          │
/// │  - Our Priority Queue approach guarantees deterministic     │
/// │    expiration ordering                                      │
/// │  - For high-scale scenarios (millions of keys), consider    │
/// │    migrating to Redis-style random sampling:                │
/// │    * Replace PriorityQueue with List<string> for O(1)       │
/// │      random access                                          │
/// │    * Use "swap-remove" technique to clean stale entries     │
/// │      during sampling                                        │
/// │    * Trade deterministic ordering for better scalability    │
/// │                                                             │
/// │  Pros: Scales to millions of keys, constant time            │
/// │  Cons: Non-deterministic expiration timing                  │
/// └─────────────────────────────────────────────────────────────┘
///
/// Besides these, a heap can become the bottleneck with a large number of timers.
/// Here are some additional optimizations to consider:
/// - Cache Friendly:
///     Binary heaps are usually stored in arrays, but their access pattern (parent i -> children 2i, 2i+1)
///     can cause cache misses. Using an N-ary heap (e.g., 4-ary heap) or quadtree helps keep data within a cache line (64 bytes), speeding up memory access.
/// - Coarse-grained Timers:
///     Instead of per-millisecond accuracy, group keys expiring within the same second (or minute) into a "bucket".
///     The heap only manages these buckets. The number of heap elements drops from millions (keys) to thousands (buckets).
///     However, accuracy decreases and you need to check the real TTL when accessing a key (lazy check).
/// - Consider researching the Timing Wheel structure—used in Kafka and Netty to efficiently manage millions of timers (O(1)).
/// 
/// Thread Safety: Not thread-safe (relies on single-threaded event loop).
/// For multi-threaded scenarios, add lock protection.
/// </summary>
public class ExpirationManager
{
    // Min-heap ordered by expiration time (earliest expiration at root)
    // Can contain duplicate keys due to lazy update strategy
    private readonly PriorityQueue<string, long> _ttlQueue = new();

    // Authoritative source of truth for key expiration times
    // Used to validate entries from the heap (filtering out stale duplicates)
    private readonly Dictionary<string, long> _keyExpirations = new();

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
        var expireAt = TimeHelper.GetNow() + durationMs;

        // Update the authoritative expiration time in the dictionary
        // This overwrites any previous expiration for this key
        _keyExpirations[key] = expireAt;

        // Add the entry to the min-heap (ordered by expiration time)
        _ttlQueue.Enqueue(key, expireAt);
    }

    /// <summary>
    /// Checks if a key has expired (passive expiration).
    ///
    /// This implements "lazy expiration" - checking when a key is accessed
    /// rather than proactively scanning all keys.
    ///
    /// Called by any command that needs to check if a key is valid (Get, Exists, etc.)
    /// 
    /// Note: This only removes the expiration metadata, not the key itself.
    /// The caller is responsible for deleting the key from the data store.
    /// </summary>
    public bool IsExpired(string key)
    {
        // Check if this key has an expiration time set
        if (!_keyExpirations.TryGetValue(key, out long expireAt))
            return false;

        // Check if the current time has passed the expiration time
        if (TimeHelper.GetNow() > expireAt)
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
    public long? GetTimeToLive(string key)
    {
        // Check if the key has an expiration time
        if (!_keyExpirations.TryGetValue(key, out long expireAt))
            return null;

        // Calculate time remaining
        var ttl = expireAt - TimeHelper.GetNow();

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
    /// Follow Lazy Update Strategy
    ///
    /// Performance: O(1) - just a dictionary removal
    /// </summary>
    public void RemoveExpiration(string key)
    {
        // Remove from the authoritative dictionary
        _keyExpirations.Remove(key);
    }

    /// <summary>
    /// Gets the time in milliseconds until the next key expires.
    ///
    /// This is used by the event loop to determine how long to sleep.
    /// By sleeping for exactly this amount of time, the loop wakes up precisely when
    /// a key needs to be expired, avoiding both busy-waiting and delayed expiration.
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
            return 10000;

        // Peek at the root of the heap (earliest expiration)
        if (_ttlQueue.TryPeek(out _, out long nextExpire))
        {
            var now = TimeHelper.GetNow();

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
        var now = TimeHelper.GetNow();

        // Work limit to prevent blocking the event loop
        // Similar to Redis's ACTIVE_EXPIRE_CYCLE_LOOKUPS_PER_LOOP
        var maxWork = 100;

        // Process up to maxWork expired keys
        while (_ttlQueue.Count > 0 && maxWork > 0)
        {
            // Peek at the root (earliest expiration)
            if (_ttlQueue.TryPeek(out _, out var priority))
            {
                // If root hasn't expired yet, we're done
                // (everything else expires even later)
                if (priority > now)
                    break;
            }

            // Pop the earliest expiration from the heap
            var key = _ttlQueue.Dequeue();

            // Validate against the dictionary (authoritative source of truth)
            if (_keyExpirations.TryGetValue(key, out var actualExpire))
            {
                // Check if this entry is current and expired
                if (actualExpire <= now)
                {
                    // This is a validation expired key
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
}
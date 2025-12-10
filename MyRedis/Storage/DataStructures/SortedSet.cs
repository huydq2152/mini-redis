namespace MyRedis.Storage.DataStructures;

/// <summary>
/// Implementation of Redis Sorted Set data structure using hybrid dictionary + AVL tree approach.
/// Provides O(log n) operations for insertion, lookup, and range queries while maintaining
/// the exact semantics expected by Redis ZADD and ZRANGE commands.
///
/// Architecture Overview:
/// - Dictionary<string, double>: Fast O(1) key-to-score lookups and membership testing
/// - AvlTree: Maintains sorted order by score for efficient range operations
/// - Dual data structure approach optimizes for both key-based and range-based queries
///
/// Redis Sorted Set Properties:
/// - Members are unique (no duplicate keys allowed)
/// - Members are ordered by score (ascending), then by key (lexicographical) for ties
/// - Supports negative indices (-1 = last element, -2 = second to last, etc.)
/// - Range operations return members in sorted order
///
/// Performance Characteristics:
/// - Add/Remove: O(log n) due to AVL tree operations
/// - Score Lookup: O(1) via dictionary
/// - Range Query: O(log n + k) where k = number of elements in range
/// - Memory: O(n) for n unique members (stored in both structures)
///
/// Thread Safety:
/// This implementation is NOT thread-safe. External synchronization required
/// for concurrent access (handled by InMemoryDataStore locking).
///
/// Future Enhancements:
/// - Support for score updates (currently simplified to ignore duplicates)
/// - Member removal operations for DEL command
/// - Score-based range queries (ZRANGEBYSCORE)
/// - Rank-based member lookup (ZRANK)
/// </summary>
public class SortedSet
{
    /// <summary>
    /// Dictionary for fast O(1) key-to-score mapping and membership testing.
    /// Enables efficient implementation of Redis commands that need to check
    /// if a member exists or retrieve its score without tree traversal.
    /// </summary>
    private readonly Dictionary<string, double> _dict = new Dictionary<string, double>();
    
    /// <summary>
    /// AVL tree maintaining members in sorted order by score and key.
    /// Provides O(log n) insertion and O(log n + k) range query performance.
    /// Essential for efficient ZRANGE command implementation.
    /// </summary>
    private readonly AvlTree _tree = new AvlTree();

    /// <summary>
    /// Gets the number of members in the sorted set.
    /// This property provides O(1) access to the cardinality of the set.
    /// </summary>
    /// <remarks>
    /// Used for:
    /// - Determining deletion strategy (sync vs async based on threshold)
    /// - ZCARD command implementation (returns sorted set cardinality)
    /// - Performance optimization decisions
    /// </remarks>
    public int Count => _dict.Count;

    /// <summary>
    /// Adds a new member with the specified score to the sorted set.
    /// This method implements the core functionality of the Redis ZADD command.
    /// </summary>
    /// <param name="key">The member name (unique identifier)</param>
    /// <param name="score">The numeric score for sorting</param>
    /// <returns>
    /// true if the member was newly added to the set.
    /// false if the member already exists (duplicate key).
    /// </returns>
    /// <remarks>
    /// Current Implementation (Simplified):
    /// - Rejects duplicate keys without updating scores
    /// - Real Redis behavior: updates the score of existing members
    /// 
    /// Algorithm:
    /// 1. Check dictionary for existing membership (O(1))
    /// 2. If new, add to both dictionary and AVL tree (O(log n))
    /// 3. Tree insertion maintains sorted order and balance
    /// 
    /// Redis Compatibility Notes:
    /// - In actual Redis, ZADD returns count of newly added elements
    /// - Score updates would require removing old entry and inserting new one
    /// - Future enhancement: implement score update logic
    /// </remarks>
    public bool Add(string key, double score)
    {
        // Simplified logic: reject duplicates rather than updating scores
        // Real Redis behavior: update existing member's score if key exists
        if (_dict.ContainsKey(key)) return false;

        // Add to both data structures to maintain consistency
        _dict[key] = score;
        _tree.Add(key, score);
        return true;
    }

    /// <summary>
    /// Retrieves the score associated with a member in the sorted set.
    /// Provides O(1) lookup performance for score-based queries.
    /// </summary>
    /// <param name="key">The member name to look up</param>
    /// <returns>
    /// The score associated with the member if found, null if the member doesn't exist.
    /// </returns>
    /// <remarks>
    /// Used by Redis commands that need member score information:
    /// - ZSCORE: Get score of a specific member
    /// - Conditional operations based on current scores
    /// - Score validation before updates
    /// 
    /// Performance: O(1) dictionary lookup, much faster than tree traversal.
    /// </remarks>
    public double? GetScore(string key)
    {
        if (_dict.TryGetValue(key, out double score)) return score;
        return null;
    }

    /// <summary>
    /// Returns members within a specified rank range in ascending score order.
    /// This implements the core functionality of the Redis ZRANGE command.
    /// </summary>
    /// <param name="start">Starting index (0-based, supports negative indices)</param>
    /// <param name="stop">Ending index (0-based, inclusive, supports negative indices)</param>
    /// <returns>
    /// List of member names in sorted order (by score, then by key).
    /// Empty list if range is invalid or no members exist.
    /// </returns>
    /// <remarks>
    /// Redis Index Semantics:
    /// - Positive indices: 0 = first element, 1 = second element, etc.
    /// - Negative indices: -1 = last element, -2 = second to last, etc.
    /// - Out-of-bounds indices are clamped to valid range
    /// - Empty range (start > stop after normalization) returns empty list
    /// 
    /// Algorithm:
    /// 1. Normalize negative indices to positive equivalents
    /// 2. Clamp indices to valid bounds [0, size-1]
    /// 3. Use AVL tree range query to extract nodes efficiently
    /// 4. Map AvlNode objects to member name strings
    /// 
    /// Performance: O(log n + k) where k = number of elements returned
    /// - O(log n) to locate range boundaries in tree
    /// - O(k) to extract and convert k elements to strings
    /// 
    /// Examples:
    /// - Range(0, 2): First 3 elements
    /// - Range(-3, -1): Last 3 elements  
    /// - Range(1, -2): All except first and last elements
    /// </remarks>
    public List<string> Range(int start, int stop)
    {
        // Handle negative indices (Redis semantics: -1 = last element)
        int size = _tree.Root?.Size ?? 0;
        if (start < 0) start += size;
        if (stop < 0) stop += size;

        // Clamp indices to valid bounds to prevent out-of-range access
        if (start < 0) start = 0;
        if (stop >= size) stop = size - 1;

        var resultNodes = new List<AvlNode>();
        if (start <= stop && _tree.Root != null)
        {
            _tree.GetRange(_tree.Root, start, stop, resultNodes);
        }

        // Convert AvlNode objects to member name strings for Redis protocol
        return resultNodes.Select(n => n.Key).ToList();
    }
}
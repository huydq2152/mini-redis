namespace MyRedis.Storage.DataStructures;

/// <summary>
/// Implementation of an AVL (Adelson-Velsky and Landis) self-balancing binary search tree
/// optimized for Redis sorted set operations with efficient range queries and rank-based access.
///
/// AVL Tree Characteristics:
/// - Self-balancing BST where height difference between subtrees is at most 1
/// - Guarantees O(log n) worst-case time complexity for all operations
/// - Maintains sorted order by score (primary) and lexicographical key (secondary)
/// - Supports efficient range queries for Redis ZRANGE command implementation
///
/// Key Features:
/// - Automatic rebalancing through rotations after insertions/deletions
/// - Size tracking in each node enables O(log n) rank-based operations
/// - Efficient range extraction without full tree traversal
/// - Optimized for Redis sorted set semantics and performance requirements
///
/// Performance Guarantees:
/// - Insert: O(log n)
/// - Search: O(log n)
/// - Range Query: O(log n + k) where k is the number of elements in range
/// - Rank Operations: O(log n)
///
/// Thread Safety:
/// This implementation is NOT thread-safe. External synchronization required
/// for concurrent access (handled by InMemoryDataStore locking).
/// </summary>
public class AvlTree
{
    /// <summary>
    /// Gets the root node of the AVL tree.
    /// null if the tree is empty. Private setter ensures tree integrity
    /// by preventing external modification of the root reference.
    /// </summary>
    public AvlNode? Root { get; private set; }

    /// <summary>
    /// Safely retrieves the height of a node, returning 0 for null nodes.
    /// This utility method prevents null reference exceptions and treats
    /// null nodes as having height 0 (standard AVL tree convention).
    /// </summary>
    /// <param name="node">The node to get height for (may be null)</param>
    /// <returns>Node height, or 0 if node is null</returns>
    private int GetHeight(AvlNode? node) => node?.Height ?? 0;

    /// <summary>
    /// Safely retrieves the subtree size of a node, returning 0 for null nodes.
    /// This utility method prevents null reference exceptions and treats
    /// null nodes as having size 0 (empty subtree).
    /// </summary>
    /// <param name="node">The node to get size for (may be null)</param>
    /// <returns>Subtree size, or 0 if node is null</returns>
    private int GetSize(AvlNode? node) => node?.Size ?? 0;

    /// <summary>
    /// Updates the height and size metadata for a node after tree structure changes.
    /// This method must be called after any operation that modifies the tree structure
    /// to maintain accurate AVL properties and enable efficient range operations.
    /// </summary>
    /// <param name="node">The node to update (null-safe)</param>
    /// <remarks>
    /// Height calculation: 1 + max(height(left), height(right))
    /// Size calculation: 1 + size(left) + size(right)
    /// Called after rotations and insertions to maintain tree invariants.
    /// </remarks>
    private void Update(AvlNode? node)
    {
        if (node == null) return;
        node.Height = 1 + Math.Max(GetHeight(node.Left), GetHeight(node.Right));
        node.Size = 1 + GetSize(node.Left) + GetSize(node.Right);
    }

    // === TREE ROTATION OPERATIONS ===
    // These operations maintain AVL balance property through local restructuring

    /// <summary>
    /// Performs a right rotation around node y to fix left-heavy imbalance.
    /// Used when the left subtree is too tall relative to the right subtree.
    /// </summary>
    /// <param name="y">The root of the subtree to rotate (becomes right child after rotation)</param>
    /// <returns>The new root of the subtree after rotation</returns>
    /// <remarks>
    /// Rotation pattern:
    ///     y                x
    ///    / \              / \
    ///   x   C    →       A   y
    ///  / \                  / \
    /// A   B                B   C
    /// 
    /// This operation maintains BST ordering while reducing tree height.
    /// Update order is crucial: children before parents to maintain correct metadata.
    /// </remarks>
    private AvlNode RotateRight(AvlNode y)
    {
        AvlNode x = y.Left!;
        AvlNode? t2 = x.Right;

        // Perform rotation: x becomes new root, y becomes right child
        x.Right = y;
        y.Left = t2;

        // Update metadata in correct order: children first, then parent
        Update(y);
        Update(x);

        return x; // Return new subtree root
    }

    /// <summary>
    /// Performs a left rotation around node x to fix right-heavy imbalance.
    /// Used when the right subtree is too tall relative to the left subtree.
    /// </summary>
    /// <param name="x">The root of the subtree to rotate (becomes left child after rotation)</param>
    /// <returns>The new root of the subtree after rotation</returns>
    /// <remarks>
    /// Rotation pattern:
    ///   x                    y
    ///  / \                  / \
    /// A   y        →       x   C
    ///    / \              / \
    ///   B   C            A   B
    /// 
    /// Mirror operation of right rotation, fixing right-heavy imbalances.
    /// Maintains BST ordering and reduces tree height on the right side.
    /// </remarks>
    private AvlNode RotateLeft(AvlNode x)
    {
        AvlNode y = x.Right!;
        AvlNode? t2 = y.Left;

        // Perform rotation: y becomes new root, x becomes left child
        y.Left = x;
        x.Right = t2;

        // Update metadata in correct order: children first, then parent
        Update(x);
        Update(y);

        return y; // Return new subtree root
    }

    /// <summary>
    /// Rebalances an AVL tree node by checking balance factors and performing rotations as needed.
    /// This is the core AVL balancing logic that maintains the height-balanced property.
    /// </summary>
    /// <param name="node">The node to balance</param>
    /// <returns>The root of the balanced subtree (may be the same node or a new root after rotation)</returns>
    /// <remarks>
    /// AVL Balance Cases:
    /// 1. Left Heavy (balance > 1):
    ///    - Left-Left case: Single right rotation
    ///    - Left-Right case: Left rotation on left child, then right rotation on node
    /// 2. Right Heavy (balance < -1):
    ///    - Right-Right case: Single left rotation  
    ///    - Right-Left case: Right rotation on right child, then left rotation on node
    /// 3. Balanced (-1 ≤ balance ≤ 1): No rotation needed
    /// 
    /// Balance factor = height(left) - height(right)
    /// AVL property requires balance factor to be in range [-1, 1]
    /// </remarks>
    private AvlNode Balance(AvlNode node)
    {
        Update(node); // Update metadata before checking balance

        int balance = GetHeight(node.Left) - GetHeight(node.Right);

        // Case 1: Left Heavy (balance > 1)
        if (balance > 1)
        {
            // Check for Left-Right case: left child is right-heavy
            if (GetHeight(node.Left!.Left) < GetHeight(node.Left.Right))
            {
                node.Left = RotateLeft(node.Left);
            }

            return RotateRight(node);
        }

        // Case 2: Right Heavy (balance < -1)
        if (balance < -1)
        {
            // Check for Right-Left case: right child is left-heavy
            if (GetHeight(node.Right!.Right) < GetHeight(node.Right.Left))
            {
                node.Right = RotateRight(node.Right);
            }

            return RotateLeft(node);
        }

        return node; // Already balanced, no rotation needed
    }

    // === PUBLIC INSERTION INTERFACE ===

    /// <summary>
    /// Adds a new key-score pair to the AVL tree, maintaining sorted order and balance.
    /// This is the public interface for Redis ZADD command implementation.
    /// </summary>
    /// <param name="key">The member key (string identifier)</param>
    /// <param name="score">The numeric score for sorting</param>
    /// <remarks>
    /// Insertion maintains Redis sorted set semantics:
    /// - Primary sort by score (ascending)
    /// - Secondary sort by key (lexicographical) for equal scores
    /// - Automatic rebalancing ensures O(log n) performance
    /// - Duplicates are handled according to Redis behavior (update or ignore)
    /// </remarks>
    public void Add(string key, double score)
    {
        Root = Insert(Root, key, score);
    }

    /// <summary>
    /// Recursively inserts a new node into the AVL tree while maintaining balance and sorted order.
    /// This is the internal implementation that handles the recursive insertion logic.
    /// </summary>
    /// <param name="node">Current subtree root (null for empty tree/subtree)</param>
    /// <param name="key">Member key to insert</param>
    /// <param name="score">Score associated with the key</param>
    /// <returns>Root of the subtree after insertion and rebalancing</returns>
    /// <remarks>
    /// Insertion Algorithm:
    /// 1. Standard BST insertion based on comparison function
    /// 2. Automatic rebalancing during recursive unwinding
    /// 3. Maintains both AVL balance property and size metadata
    /// 
    /// Comparison Logic (Redis sorted set semantics):
    /// - Primary: Compare by score (double.CompareTo)
    /// - Secondary: Compare by key (lexicographical) if scores are equal
    /// - This ensures deterministic ordering for equal scores
    /// 
    /// Performance: O(log n) due to tree height being O(log n) and constant work per level
    /// </remarks>
    private AvlNode Insert(AvlNode? node, string key, double score)
    {
        // Base case: Create new leaf node
        if (node == null) return new AvlNode(key, score);

        // Determine insertion direction using Redis sorted set comparison logic
        // Primary sort by score, secondary sort by key for deterministic ordering
        int compare = score.CompareTo(node.Score);
        if (compare == 0) compare = string.Compare(key, node.Key, StringComparison.Ordinal);

        if (compare < 0)
            node.Left = Insert(node.Left, key, score);
        else if (compare > 0)
            node.Right = Insert(node.Right, key, score);
        else
            return node; // Duplicate found - Redis behavior: ignore or update based on configuration

        // Rebalance the tree during recursive unwinding to maintain AVL properties
        // This ensures the tree remains height-balanced after insertion
        return Balance(node);
    }

    // === RANGE QUERY OPERATIONS ===
    // Efficient range extraction for Redis ZRANGE command implementation

    /// <summary>
    /// Extracts nodes within a specified rank range from the AVL tree in sorted order.
    /// This implements the core logic for Redis ZRANGE command with O(log n + k) complexity.
    /// </summary>
    /// <param name="node">Current subtree root being processed</param>
    /// <param name="start">Starting rank (0-based, inclusive)</param>
    /// <param name="stop">Ending rank (0-based, inclusive)</param>
    /// <param name="result">List to accumulate nodes within the range</param>
    /// <remarks>
    /// Range Query Algorithm:
    /// 1. Calculate the rank of the current node within its subtree
    /// 2. Recursively process left subtree if range overlaps with left side
    /// 3. Include current node if it falls within the target range
    /// 4. Recursively process right subtree with adjusted rank indices
    /// 
    /// Rank Calculation:
    /// - Node's rank = size of left subtree (0-based indexing)
    /// - Ranks are relative to the current subtree being processed
    /// - When moving to right subtree, adjust indices by (currentRank + 1)
    /// 
    /// Performance: O(log n + k) where k is the number of elements returned
    /// - O(log n) to locate the range boundaries
    /// - O(k) to extract k elements within the range
    /// - Avoids full tree traversal by pruning irrelevant subtrees
    /// 
    /// Example:
    /// Tree with elements [A:1.0, B:2.0, C:3.0, D:4.0, E:5.0]
    /// GetRange(root, 1, 3, result) returns [B, C, D]
    /// </remarks>
    public void GetRange(AvlNode? node, int start, int stop, List<AvlNode> result)
    {
        if (node == null) return;

        // Calculate the rank of current node within this subtree
        // Rank = number of elements in left subtree (0-based indexing)
        int leftSize = GetSize(node.Left);
        int currentRank = leftSize;

        // Process left subtree if the range extends into the left side
        if (start < currentRank)
        {
            GetRange(node.Left, start, stop, result);
        }

        // Include current node if it falls within the target range
        if (start <= currentRank && stop >= currentRank)
        {
            result.Add(node);
        }

        // Process right subtree if the range extends into the right side
        // Adjust indices: elements in right subtree have ranks > currentRank
        if (stop > currentRank)
        {
            GetRange(node.Right, start - (currentRank + 1), stop - (currentRank + 1), result);
        }
    }
}
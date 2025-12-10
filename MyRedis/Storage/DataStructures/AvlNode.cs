namespace MyRedis.Storage.DataStructures;

/// <summary>
/// Represents a node in an AVL (Adelson-Velsky and Landis) self-balancing binary search tree.
/// Each node stores a key-score pair for Redis sorted set operations and maintains
/// AVL tree metadata for automatic balancing and efficient range queries.
///
/// AVL Tree Properties:
/// - Self-balancing binary search tree with height difference ≤ 1 between subtrees
/// - Guarantees O(log n) time complexity for insertion, deletion, and search operations
/// - Maintains sorted order by score (primary) and lexicographical key order (secondary)
/// - Supports efficient range queries and rank-based operations for Redis ZRANGE command
///
/// Node Structure:
/// - Business Data: Key (member name) and Score (numeric value for sorting)
/// - AVL Metadata: Height and Size for tree balancing and range operations
/// - Tree Links: Left and Right child pointers for tree structure
///
/// Sorting Logic:
/// Nodes are ordered first by score (ascending), then by key (lexicographical) for ties.
/// This matches Redis sorted set semantics where members with equal scores are sorted alphabetically.
///
/// Size Tracking:
/// Each node maintains the size of its subtree (including itself) to enable:
/// - O(log n) rank-based access (find the k-th smallest element)
/// - Efficient ZRANGE operations with start/stop indices
/// - Quick size calculations without tree traversal
/// </summary>
public class AvlNode
{
    /// <summary>
    /// Gets or sets the member key (string identifier) stored in this node.
    /// This represents the member name in a Redis sorted set.
    /// Used as secondary sort key when scores are equal (lexicographical ordering).
    /// </summary>
    public string Key { get; set; }
    
    /// <summary>
    /// Gets or sets the numeric score associated with this member.
    /// This is the primary sorting criterion for the sorted set.
    /// Members are ordered by score (ascending), with lexicographical key ordering for ties.
    /// </summary>
    public double Score { get; set; }

    /// <summary>
    /// Gets or sets the height of this node in the AVL tree.
    /// Height is defined as the maximum distance from this node to any leaf node.
    /// Used for AVL balancing to ensure the tree remains approximately balanced.
    /// Leaf nodes have height 1, and height increases going up the tree.
    /// </summary>
    public int Height { get; set; } = 1;
    
    /// <summary>
    /// Gets or sets the size of the subtree rooted at this node (including the node itself).
    /// This enables O(log n) rank-based operations and efficient range queries.
    /// Size = 1 + Size(LeftSubtree) + Size(RightSubtree)
    /// Used by ZRANGE to convert indices to actual tree positions.
    /// </summary>
    public int Size { get; set; } = 1;

    /// <summary>
    /// Gets or sets the left child node.
    /// Contains nodes with smaller scores, or smaller keys if scores are equal.
    /// null if this node has no left child.
    /// </summary>
    public AvlNode? Left { get; set; }
    
    /// <summary>
    /// Gets or sets the right child node.
    /// Contains nodes with larger scores, or larger keys if scores are equal.
    /// null if this node has no right child.
    /// </summary>
    public AvlNode? Right { get; set; }

    /// <summary>
    /// Initializes a new AVL tree node with the specified key and score.
    /// The node starts as a leaf with height=1 and size=1.
    /// </summary>
    /// <param name="key">The member key (string identifier)</param>
    /// <param name="score">The numeric score for sorting</param>
    public AvlNode(string key, double score)
    {
        Key = key;
        Score = score;
    }
}
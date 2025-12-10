namespace MyRedis.Core
{
    /// <summary>
    /// Manages idle connection detection using an intrusive linked list.
    ///
    /// Purpose: Automatically detect and close connections that haven't sent
    /// any data for longer than the idle timeout to prevent resource exhaustion.
    ///
    /// Data Structure: Intrusive Doubly-Linked List
    /// - "Intrusive" means each Connection stores its own LinkedListNode reference
    /// - This enables O(1) removal from any position in the list
    /// - Connections are ordered by last activity time (oldest first, newest last)
    ///
    /// Why Linked List Instead of Other Structures?
    /// - Array/List: O(n) to remove from middle, O(1) to add at end
    /// - Heap: O(log n) to update, complex for this use case
    /// - Linked List: O(1) for all operations (add, remove, move to end)
    ///
    /// How It Works:
    /// 1. New connections are added to the tail (most recent)
    /// 2. When data arrives, connection moves to tail (Touch operation)
    /// 3. Head always contains the oldest (least recently active) connection
    /// 4. Scan from head to find idle connections (stops at first non-idle)
    ///
    /// Time Complexity:
    /// - Add: O(1)
    /// - Remove: O(1)
    /// - Touch (move to end): O(1)
    /// - GetIdleConnections: O(k) where k = number of idle connections
    ///
    /// Thread Safety: Not thread-safe (relies on single-threaded event loop).
    /// </summary>
    public class IdleManager
    {
        // Doubly-linked list ordered by last activity time
        // Head = oldest (least recently active)
        // Tail = newest (most recently active)
        private readonly LinkedList<Connection> _list = new LinkedList<Connection>();

        // Idle timeout in milliseconds (5 minutes)
        // Connections inactive for longer than this will be closed
        private const int IdleTimeoutMs = 300 * 1000;

        /// <summary>
        /// Adds a new connection to the idle tracking list.
        ///
        /// Called by NetworkServer immediately after accepting a new client connection.
        ///
        /// Operation:
        /// 1. Set the connection's LastActive timestamp to now
        /// 2. Add to the END of the list (tail = most recent)
        /// 3. Store the LinkedListNode in the connection (intrusive pattern)
        ///
        /// The intrusive pattern (storing the node in the connection) enables
        /// O(1) removal later without searching through the list.
        ///
        /// Performance: O(1)
        /// </summary>
        public void Add(Connection conn)
        {
            // Mark the connection as active right now
            conn.LastActive = Environment.TickCount64;

            // Add to the END of the list (newest connections at tail)
            // Store the node reference in the connection for O(1) removal later
            conn.Node = _list.AddLast(conn);
        }

        /// <summary>
        /// Updates the last activity time and moves the connection to the end of the list.
        ///
        /// Called by NetworkServer every time data is received from a client.
        /// This "resets" the idle timer for this connection.
        ///
        /// Operation:
        /// 1. Update LastActive timestamp to now
        /// 2. Remove connection from its current position in the list
        /// 3. Add it back at the END (tail = most recent)
        ///
        /// Why move to end?
        /// - Maintains the invariant that the list is ordered by activity time
        /// - Head always contains the least recently active connection
        /// - Enables efficient idle detection (scan from head, stop at first non-idle)
        ///
        /// Intrusive List Advantage:
        /// - We have direct access to the node (conn.Node)
        /// - LinkedList.Remove(node) is O(1) - just update prev/next pointers
        /// - No need to search the list
        ///
        /// Performance: O(1)
        /// </summary>
        public void Touch(Connection conn)
        {
            // Update the activity timestamp
            conn.LastActive = Environment.TickCount64;

            // Verify the connection is still in this list
            if (conn.Node != null && conn.Node.List == _list)
            {
                // Remove from current position (O(1) - we have the node reference)
                _list.Remove(conn.Node);

                // Add back at the end (tail = most recently active)
                _list.AddLast(conn.Node);
            }
        }

        /// <summary>
        /// Removes a connection from idle tracking.
        ///
        /// Called by NetworkServer when:
        /// - Client disconnects normally
        /// - Connection error occurs
        /// - Connection is being closed due to idle timeout
        ///
        /// Operation:
        /// 1. Remove the node from the linked list
        /// 2. Clear the node reference in the connection
        ///
        /// Intrusive List Advantage:
        /// - Direct node access enables O(1) removal
        /// - No need to search through the list to find the connection
        ///
        /// Performance: O(1)
        /// </summary>
        public void Remove(Connection conn)
        {
            // Verify the connection is still in this list
            if (conn.Node != null && conn.Node.List == _list)
            {
                // Remove from the list (O(1))
                _list.Remove(conn.Node);

                // Clear the node reference
                conn.Node = null;
            }
        }

        /// <summary>
        /// Gets all connections that have been idle for longer than the timeout.
        ///
        /// Called by BackgroundTaskManager on each event loop iteration.
        ///
        /// Algorithm:
        /// 1. Start from the HEAD (oldest connections)
        /// 2. Check if idle time exceeds threshold
        /// 3. If idle: add to result and remove from list
        /// 4. If not idle: STOP (all subsequent connections are newer)
        ///
        /// Early Termination Optimization:
        /// - The list is ordered by activity time (oldest first)
        /// - As soon as we find a non-idle connection, we can stop
        /// - All connections after it are guaranteed to be even newer
        /// - This makes the typical case very fast (usually 0-2 idle connections)
        ///
        /// Removal During Iteration:
        /// - We remove idle connections from the list as we find them
        /// - This prevents them from being checked again
        /// - We save the next node before removal to continue iteration safely
        ///
        /// Why Remove From List?
        /// - These connections are being closed by the caller
        /// - Keeping them in the list would be wasteful
        /// - They'll also be removed via Remove() when the socket closes
        /// - Removing here is just an optimization to avoid duplicate work
        ///
        /// Performance: O(k) where k = number of idle connections
        /// Typically k is very small (0-5), making this effectively O(1)
        ///
        /// Returns: List of connections exceeding the idle timeout (may be empty)
        /// </summary>
        public List<Connection> GetIdleConnections()
        {
            var result = new List<Connection>();
            long now = Environment.TickCount64;

            // Start from the HEAD (oldest connections first)
            var node = _list.First;
            while (node != null)
            {
                var conn = node.Value;

                // Calculate how long this connection has been idle
                long idleTime = now - conn.LastActive;

                if (idleTime > IdleTimeoutMs)
                {
                    // Connection has been idle too long - mark for closure
                    result.Add(conn);

                    // Save the next node before we remove this one
                    var next = node.Next;

                    // Remove from the list (connection will be closed by caller)
                    _list.Remove(node); // O(1)

                    // Move to the next connection
                    node = next;
                }
                else
                {
                    // This connection is still within the idle threshold
                    // Since the list is ordered by activity time (oldest first),
                    // all subsequent connections are guaranteed to be more recent
                    // and therefore not idle. We can stop here.
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the time in milliseconds until the next connection will become idle.
        ///
        /// Used by the event loop to determine how long to sleep in Socket.Select().
        /// By sleeping for exactly this amount of time, the loop wakes up precisely when
        /// a connection becomes idle, enabling timely cleanup without busy-waiting.
        ///
        /// Algorithm:
        /// 1. Look at the HEAD of the list (oldest connection)
        /// 2. Calculate when it will become idle (LastActive + Timeout)
        /// 3. Return time difference from now
        ///
        /// Return values:
        /// - 0: One or more connections are already idle (process immediately)
        /// - Positive number: Milliseconds until first connection becomes idle
        /// - 10000 (default): No connections exist (sleep for 10 seconds)
        ///
        /// Why 10 seconds default?
        /// - Prevents infinite sleep when no connections exist
        /// - Allows event loop to wake up periodically for other tasks
        /// - Balances responsiveness with CPU efficiency
        ///
        /// Performance: O(1) - just looks at list head
        /// </summary>
        public int GetNextTimeout()
        {
            // No connections? Return default timeout
            if (_list.First == null)
                return 10000; // 10 seconds

            long now = Environment.TickCount64;

            // Calculate when the oldest connection will become idle
            long deadline = _list.First.Value.LastActive + IdleTimeoutMs;

            // If already past deadline, wake up immediately
            if (deadline <= now)
                return 0;

            // Return time until this connection becomes idle
            return (int)(deadline - now);
        }
    }
}
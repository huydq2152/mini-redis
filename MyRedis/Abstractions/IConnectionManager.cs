namespace MyRedis.Abstractions;

/// <summary>
/// Service for managing connection lifecycle and idle detection.
///
/// Purpose: Automatically close connections that have been inactive for too long
/// to prevent resource exhaustion from abandoned clients.
///
/// Implementation Strategy (Intrusive Linked List):
/// - Connections are stored in a doubly-linked list ordered by last activity time
/// - Most recently active connections are at the tail
/// - Least recently active connections are at the head
/// - O(1) operations for Add, Remove, Touch
/// - O(k) operation for getting k idle connections
///
/// How It Works:
/// 1. NetworkServer calls Add() when a client connects
/// 2. NetworkServer calls Touch() every time a client sends data
/// 3. Touch() moves the connection to the end of the list (most recent)
/// 4. BackgroundTaskManager periodically calls GetIdleConnections()
/// 5. Connections idle longer than threshold are returned for cleanup
/// 6. NetworkServer calls Remove() when closing a connection
///
/// Idle Threshold: Configurable, typically 60 seconds
/// </summary>
public interface IConnectionManager
{
    /// <summary>
    /// Adds a new connection to be tracked for idle detection.
    ///
    /// Called by NetworkServer immediately after accepting a new client connection.
    /// The connection is added to the end of the activity list with current timestamp.
    /// </summary>
    /// <param name="connection">The connection to track (must not already be tracked)</param>
    void Add(Core.Connection connection);

    /// <summary>
    /// Removes a connection from tracking.
    ///
    /// Called by NetworkServer when:
    /// - Client disconnects normally
    /// - Connection error occurs
    /// - Connection is being closed due to idle timeout
    ///
    /// Removes the connection from the intrusive linked list in O(1) time.
    /// </summary>
    /// <param name="connection">The connection to stop tracking</param>
    void Remove(Core.Connection connection);

    /// <summary>
    /// Updates the last activity time for a connection and moves it to the end of the list.
    ///
    /// Called by NetworkServer every time data is received from a client.
    /// This prevents active connections from being considered idle.
    ///
    /// Operation:
    /// 1. Update connection's LastActive timestamp to current time
    /// 2. Move connection to the end of the linked list (most recent position)
    /// 3. O(1) time complexity due to intrusive linked list
    /// </summary>
    /// <param name="connection">The connection that just received data</param>
    void Touch(Core.Connection connection);

    /// <summary>
    /// Gets all connections that have been idle for longer than the configured threshold.
    ///
    /// Called by BackgroundTaskManager on each event loop iteration.
    /// Since the list is ordered by activity time, we only need to check from the head
    /// until we find a connection that's still within the idle threshold.
    ///
    /// Returned connections should be closed by the caller.
    /// </summary>
    /// <returns>List of connections exceeding the idle timeout (may be empty)</returns>
    IList<Core.Connection> GetIdleConnections();

    /// <summary>
    /// Gets the time in milliseconds until the next connection will become idle.
    ///
    /// Used by the event loop to determine how long to wait in Socket.Select().
    /// If no connections exist, returns a default timeout (e.g., 1000ms).
    ///
    /// This enables efficient idle detection without constant polling:
    /// - The event loop sleeps for exactly the right amount of time
    /// - When it wakes up, there will be idle connections to process
    /// </summary>
    /// <returns>Milliseconds until next idle check, or default timeout if no connections</returns>
    int GetNextTimeout();
}
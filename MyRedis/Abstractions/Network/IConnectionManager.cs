using MyRedis.Core.Network;

namespace MyRedis.Abstractions.Network;

/// <summary>
/// Service for managing connection lifecycle and idle detection.
/// </summary>
public interface IConnectionManager
{
    /// <summary>
    /// Adds a new connection to be tracked for idle detection.
    /// </summary>
    /// <param name="connection">The connection to track (must not already be tracked)</param>
    void Add(Connection connection);

    /// <summary>
    /// Removes a connection from tracking.
    /// </summary>
    /// <param name="connection">The connection to stop tracking</param>
    void Remove(Connection connection);

    /// <summary>
    /// Updates the last activity time for a connection and moves it to the end of the list.
    /// </summary>
    /// <param name="connection">The connection that just received data</param>
    void Touch(Connection connection);

    /// <summary>
    /// Gets all connections that have been idle for longer than the configured threshold.
    ///
    /// Returned connections should be closed by the caller.
    /// </summary> <returns>List of connections exceeding the idle timeout (perhaps empty)</returns>
    IList<Connection> GetIdleConnections();

    /// <summary>
    /// Gets the time in milliseconds until the next connection will become idle.
    /// </summary>
    /// <returns>Milliseconds until next idle check, or default timeout if no connections</returns>
    int GetNextTimeout();
}
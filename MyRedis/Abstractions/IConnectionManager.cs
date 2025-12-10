namespace MyRedis.Abstractions;

/// <summary>
/// Service for managing connection lifecycle and idle detection
/// </summary>
public interface IConnectionManager
{
    /// <summary>
    /// Adds a connection to be managed
    /// </summary>
    /// <param name="connection">The connection to add</param>
    void Add(Core.Connection connection);

    /// <summary>
    /// Removes a connection from management
    /// </summary>
    /// <param name="connection">The connection to remove</param>
    void Remove(Core.Connection connection);

    /// <summary>
    /// Updates the last active time for a connection
    /// </summary>
    /// <param name="connection">The connection to touch</param>
    void Touch(Core.Connection connection);

    /// <summary>
    /// Gets connections that have been idle for too long
    /// </summary>
    /// <returns>List of idle connections</returns>
    IList<Core.Connection> GetIdleConnections();

    /// <summary>
    /// Gets the timeout until the next idle check
    /// </summary>
    /// <returns>Timeout in milliseconds</returns>
    int GetNextTimeout();
}
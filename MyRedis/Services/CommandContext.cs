using MyRedis.Abstractions;
using MyRedis.Core;

namespace MyRedis.Services;

/// <summary>
/// Implementation of command execution context that provides all necessary services
/// and state for Redis command handlers to execute their operations.
///
/// Design Pattern: Context Object Pattern
/// This class encapsulates all per-request state and services in a single object,
/// avoiding the need to pass multiple parameters to every command handler method.
/// This makes the system more maintainable and extensible.
///
/// Lifecycle:
/// 1. Created by CommandProcessor for each incoming client command
/// 2. Populated with current connection, shared services, and request state
/// 3. Passed to the appropriate command handler for execution
/// 4. Disposed after command completion (no explicit cleanup needed)
///
/// Thread Safety:
/// - CommandContext instances are per-request and not shared between threads
/// - The services it contains (DataStore, ExpirationService) are thread-safe
/// - Connection objects are tied to specific client sockets and not shared
///
/// Usage Pattern:
/// Command handlers receive this context and use it to:
/// - Access the client's connection and write buffer
/// - Read/write data using the centralized data store
/// - Manage key expiration through the expiration service
/// - Send formatted responses using the response writer
/// </summary>
public class CommandContext : ICommandContext
{
    /// <summary>
    /// Gets the client connection associated with this command execution.
    /// Provides access to the connection's write buffer for sending responses
    /// and connection metadata for logging and monitoring purposes.
    /// </summary>
    public Connection Connection { get; }
    
    /// <summary>
    /// Gets the data store service for all key-value operations.
    /// This is the main storage abstraction where Redis data is persisted
    /// and retrieved. Supports multiple data types including strings,
    /// sorted sets, and other Redis data structures.
    /// </summary>
    public IDataStore DataStore { get; }
    
    /// <summary>
    /// Gets the expiration service for managing key time-to-live (TTL) operations.
    /// Handles setting expiration times, checking if keys have expired,
    /// and automatic cleanup of expired keys in the background.
    /// </summary>
    public IExpirationService ExpirationService { get; }
    
    /// <summary>
    /// Gets the response writer service for formatting and sending protocol responses.
    /// Handles serialization of different response types (strings, integers, arrays, errors)
    /// according to the binary protocol specification.
    /// </summary>
    public IResponseWriter ResponseWriter { get; }

    /// <summary>
    /// Initializes a new instance of the CommandContext with all required services.
    /// All parameters are mandatory as command handlers require access to all services
    /// to perform their operations correctly.
    /// </summary>
    /// <param name="connection">The client connection for this command execution</param>
    /// <param name="dataStore">The data store service for key-value operations</param>
    /// <param name="expirationService">The expiration service for TTL management</param>
    /// <param name="responseWriter">The response writer for protocol formatting</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null</exception>
    public CommandContext(
        Connection connection,
        IDataStore dataStore,
        IExpirationService expirationService,
        IResponseWriter responseWriter)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        DataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        ExpirationService = expirationService ?? throw new ArgumentNullException(nameof(expirationService));
        ResponseWriter = responseWriter ?? throw new ArgumentNullException(nameof(responseWriter));
    }
}
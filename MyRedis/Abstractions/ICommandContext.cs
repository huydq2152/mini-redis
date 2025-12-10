using MyRedis.Core;

namespace MyRedis.Abstractions;

/// <summary>
/// Provides execution context for command handlers.
///
/// This interface follows the Dependency Injection pattern, giving command handlers
/// access to all the services they need without tight coupling to concrete implementations.
///
/// Design Pattern: Context Object Pattern
/// - Encapsulates all per-request state and services in a single object
/// - Avoids passing multiple parameters to every command handler
/// - Makes it easy to add new context information without changing handler signatures
///
/// Usage: Command handlers receive an ICommandContext when executing a command,
/// allowing them to:
/// 1. Read/write data via DataStore
/// 2. Set TTL via ExpirationService
/// 3. Send responses via ResponseWriter
/// 4. Access connection info via Connection
/// </summary>
public interface ICommandContext
{
    /// <summary>
    /// Gets the connection associated with this command execution.
    ///
    /// The Connection object provides:
    /// - Write buffer for sending responses
    /// - Client socket information
    /// - Last activity timestamp (for idle detection)
    ///
    /// Use this to write responses directly to the client's write buffer.
    /// </summary>
    Connection Connection { get; }

    /// <summary>
    /// Gets the data store service for key-value operations.
    ///
    /// This is the main storage abstraction where all Redis data is kept.
    /// Implementation: InMemoryDataStore (thread-safe dictionary)
    ///
    /// Supports:
    /// - Simple values (strings, numbers)
    /// - Complex types (SortedSet, etc.)
    /// - Type-safe generic retrieval
    /// </summary>
    IDataStore DataStore { get; }

    /// <summary>
    /// Gets the expiration service for TTL management.
    ///
    /// Use this to:
    /// - Set expiration times for keys (EXPIRE command)
    /// - Check if a key is expired (used in GET, etc.)
    /// - Get remaining TTL (TTL command)
    ///
    /// Implementation: Min-heap based priority queue for O(log n) operations
    /// Background tasks automatically delete expired keys.
    /// </summary>
    IExpirationService ExpirationService { get; }

    /// <summary>
    /// Gets the response writer for sending protocol-formatted responses.
    ///
    /// The response writer handles serialization of responses into the binary protocol:
    /// - Type 0: Nil (null values)
    /// - Type 1: Error (command errors)
    /// - Type 2: String (text responses)
    /// - Type 3: Integer (numeric responses)
    /// - Type 4: Array (multi-value responses)
    ///
    /// Use this instead of writing bytes directly to maintain protocol consistency.
    /// </summary>
    IResponseWriter ResponseWriter { get; }
}
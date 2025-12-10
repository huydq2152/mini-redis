namespace MyRedis.Abstractions;

/// <summary>
/// Represents a command handler that can execute specific Redis commands.
///
/// This interface defines the contract for all Redis command implementations in the system.
/// Each command (GET, SET, DEL, PING, etc.) has its own handler class implementing this interface.
///
/// Design Pattern: Command Pattern
/// - Encapsulates each Redis command as a separate object
/// - Decouples command parsing from command execution
/// - Makes it easy to add new commands without modifying core infrastructure
/// - Enables command introspection and registration at runtime
///
/// How Command Handlers Work:
/// 1. Client sends: "GET mykey"
/// 2. ProtocolParser extracts command name and arguments: ["GET", "mykey"]
/// 3. CommandRegistry looks up handler by name: GetCommandHandler
/// 4. CommandProcessor calls HandleAsync with context and args: ["mykey"]
/// 5. Handler executes logic and writes response using context.ResponseWriter
///
/// Command Lifecycle:
/// 1. Registration: Handler is registered in CommandRegistry during server startup
/// 2. Execution: Handler is invoked for each matching command from clients
/// 3. Context: Handler receives ICommandContext with all necessary services
/// 4. Response: Handler must write exactly one response (success, error, or data)
///
/// Implementation Guidelines:
/// - Handlers should validate argument count and types
/// - Use base class error methods for consistent error responses
/// - Always write a response (never leave client waiting)
/// - Keep handlers stateless (all state is in the context)
/// - Handle both sync and async operations appropriately
/// </summary>
public interface ICommandHandler
{
    /// <summary>
    /// Gets the Redis command name that this handler processes.
    ///
    /// This property is used by the CommandRegistry to map command names to handlers.
    /// Command names are case-insensitive (GET, get, GeT are all treated the same).
    ///
    /// Examples:
    /// - "GET" for key retrieval
    /// - "SET" for key storage
    /// - "DEL" for key deletion
    /// - "PING" for connectivity testing
    /// - "ZADD" for sorted set operations
    ///
    /// The command name should match the Redis protocol specification exactly.
    /// This ensures compatibility with Redis clients and tools.
    /// </summary>
    string CommandName { get; }

    /// <summary>
    /// Executes the Redis command asynchronously with the provided context and arguments.
    ///
    /// This is the core method where command-specific logic is implemented.
    /// The handler receives a complete execution context with access to:
    /// - Data store for reading/writing key-value data
    /// - Expiration service for TTL management
    /// - Response writer for sending formatted responses to the client
    /// - Connection information for buffer management
    ///
    /// Argument Processing:
    /// - Args contains only the command arguments (command name is already removed)
    /// - For "SET mykey myvalue", args will be ["mykey", "myvalue"]
    /// - For "GET mykey", args will be ["mykey"]
    /// - For "PING", args will be an empty list
    ///
    /// Response Requirements:
    /// - Handler MUST write exactly one response using context.ResponseWriter
    /// - Responses can be: string, integer, nil, error, or array
    /// - Never leave a client request without a response
    ///
    /// Error Handling:
    /// - Validate argument count and format
    /// - Use BaseCommandHandler helper methods for consistent error responses
    /// - Handle both client errors (wrong args) and server errors (internal failures)
    ///
    /// Concurrency Notes:
    /// - Handlers may be called concurrently from different connections
    /// - All shared state access must be thread-safe
    /// - The context provides thread-safe services (DataStore, ExpirationService)
    /// </summary>
    /// <param name="context">
    /// The command execution context providing access to data store, expiration service,
    /// response writer, and connection information. This contains all services needed
    /// to execute the command and send responses.
    /// </param>
    /// <param name="args">
    /// The command arguments as parsed from the client request, excluding the command name itself.
    /// Arguments are already decoded from the protocol format into strings.
    /// The list is read-only to prevent accidental modification.
    /// </param>
    /// <returns>
    /// A task that completes when the command has been fully processed and a response
    /// has been written to the client. Returns true if the command was handled successfully
    /// (regardless of whether the command itself succeeded or failed - e.g., GET on
    /// non-existent key still returns true). Returns false only if the handler cannot
    /// process the command due to internal errors.
    /// </returns>
    Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args);
}
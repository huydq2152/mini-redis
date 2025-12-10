using MyRedis.Abstractions;
using MyRedis.Core;

namespace MyRedis.Infrastructure;

/// <summary>
/// Parses Redis protocol and routes commands to their handlers.
///
/// Responsibility: Protocol Processing and Command Routing
/// - Parse binary Redis protocol from connection buffers
/// - Route commands to registered handlers
/// - Support pipelining (multiple commands in one TCP packet)
/// - Build command execution context for handlers
///
/// Protocol Format:
/// MyRedis uses a simplified binary protocol:
/// [4-byte count][4-byte len][string][4-byte len][string]...
///
/// Example: SET key value
/// - Count: 3 (3 arguments)
/// - Len: 3, Data: "SET"
/// - Len: 3, Data: "key"
/// - Len: 5, Data: "value"
///
/// Integration with Event Loop:
/// 1. NetworkServer.ProcessNetworkEvents() reads data into connection buffers
/// 2. RedisServerOrchestrator calls ProcessConnectionDataAsync() for each connection
/// 3. CommandProcessor parses and executes all commands in the buffer
/// 4. Responses are written to connection write buffers and flushed
///
/// Pipelining Support:
/// Clients can send multiple commands without waiting for responses:
/// - Client sends: "GET key1\nGET key2\nGET key3" in one packet
/// - Server processes all three commands
/// - Server sends back all three responses
/// - This dramatically improves throughput (batch processing)
///
/// Command Execution Flow:
/// 1. Parse command from buffer using ProtocolParser
/// 2. Look up handler in CommandRegistry
/// 3. Build ICommandContext with connection, data store, expiration service
/// 4. Call handler.HandleAsync(context, arguments)
/// 5. Handler writes response to connection.WriteBuffer
/// 6. Flush response immediately (send to client)
/// 7. Remove parsed bytes from buffer and repeat
///
/// Error Handling:
/// - Unknown command: Returns error response to client
/// - Parse error: Waits for more data (partial command)
/// - Handler exception: Should be caught by handler (returns error response)
///
/// Performance Considerations:
/// - Zero-copy parsing: ProtocolParser reads directly from connection buffer
/// - Buffer compaction: ShiftBuffer() removes processed bytes
/// - Immediate flush: Responses sent as soon as command completes
/// - No unnecessary allocations: Reuses connection buffers
///
/// Design Pattern: Command Pattern + Strategy Pattern
/// - Command Pattern: Each command is a separate handler object
/// - Strategy Pattern: CommandRegistry selects the right handler
/// - This makes adding new commands trivial (just register a new handler)
/// </summary>
public class CommandProcessor
{
    // Registry that maps command names (GET, SET, etc.) to handler instances
    private readonly ICommandRegistry _commandRegistry;

    // Data store where key-value pairs are stored
    private readonly IDataStore _dataStore;

    // Service that manages TTL (time-to-live) for keys with expiration
    private readonly IExpirationService _expirationService;

    // Service that formats responses in Redis protocol
    private readonly IResponseWriter _responseWriter;

    /// <summary>
    /// Creates a command processor with all required dependencies.
    ///
    /// Dependencies are injected to maintain loose coupling and testability.
    /// All parameters are required (null check enforced).
    ///
    /// Why These Dependencies?
    /// - CommandRegistry: Looks up handlers for command names (GET, SET, etc.)
    /// - DataStore: Passed to handlers so they can read/write data
    /// - ExpirationService: Passed to handlers that manage TTLs (EXPIRE, TTL, etc.)
    /// - ResponseWriter: Passed to handlers so they can format responses
    /// </summary>
    public CommandProcessor(
        ICommandRegistry commandRegistry,
        IDataStore dataStore,
        IExpirationService expirationService,
        IResponseWriter responseWriter)
    {
        // Validate all dependencies (fail-fast if misconfigured)
        _commandRegistry = commandRegistry ?? throw new ArgumentNullException(nameof(commandRegistry));
        _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        _expirationService = expirationService ?? throw new ArgumentNullException(nameof(expirationService));
        _responseWriter = responseWriter ?? throw new ArgumentNullException(nameof(responseWriter));
    }

    /// <summary>
    /// Processes all commands in the connection's read buffer.
    ///
    /// This method is called by RedisServerOrchestrator after NetworkServer detects
    /// that a connection has received data.
    ///
    /// Processing Flow:
    /// 1. Try to parse one command from the buffer
    /// 2. If successful:
    ///    a. Execute the command (call handler)
    ///    b. Flush response to client
    ///    c. Remove parsed bytes from buffer
    ///    d. Repeat (handle pipelining)
    /// 3. If parsing fails:
    ///    a. Partial command in buffer
    ///    b. Wait for more data from client
    ///    c. Exit loop and return
    ///
    /// Pipelining Example:
    /// Client sends: GET key1; GET key2; GET key3 (all in one TCP packet)
    /// - Loop iteration 1: Parse and execute GET key1
    /// - Loop iteration 2: Parse and execute GET key2
    /// - Loop iteration 3: Parse and execute GET key3
    /// - Loop iteration 4: No more complete commands, exit
    ///
    /// Why Loop Until Break?
    /// - Client may send multiple commands in one packet (pipelining)
    /// - We want to process all available commands immediately
    /// - This improves throughput and reduces latency
    ///
    /// Buffer Management:
    /// - connection.ReadBuffer: Contains raw bytes from client
    /// - connection.BytesRead: How many valid bytes in buffer
    /// - ShiftBuffer(consumed): Removes processed bytes, compacts remaining
    ///
    /// Performance:
    /// - Zero-copy parsing: ProtocolParser reads directly from buffer
    /// - Immediate response: Flush after each command (low latency)
    /// - Efficient loop: Only processes what's available, no blocking
    /// </summary>
    /// <param name="connection">The connection that received data</param>
    /// <returns>Number of commands processed (for metrics/logging)</returns>
    public async Task<int> ProcessConnectionDataAsync(Connection connection)
    {
        // Track how many commands we process (for metrics and logging)
        int commandsProcessed = 0;

        // Loop to handle pipelining: client may send multiple commands at once
        // Keep processing until we can't parse a complete command
        while (true)
        {
            // Try to parse one command from the buffer
            // TryParse returns:
            // - true: Successfully parsed a command (cmd) and consumed bytes
            // - false: Not enough data for a complete command (need more from client)
            if (ProtocolParser.TryParse(connection.ReadBuffer, connection.BytesRead,
                out var cmd, out int consumed))
            {
                // Successfully parsed one complete command
                // cmd is a list of strings: ["GET", "key"] or ["SET", "key", "value"]
                Console.WriteLine($"[Command] {string.Join(" ", cmd)}");

                // Execute the command (call the appropriate handler)
                await ExecuteCommandAsync(connection, cmd);

                // Send response immediately to the client
                // This ensures low latency (don't buffer responses)
                connection.Flush();

                // Remove processed bytes from the buffer
                // This compacts the buffer so remaining data moves to the front
                // If buffer had [cmd1][cmd2][partial], after shift: [cmd2][partial]
                connection.ShiftBuffer(consumed);

                // Increment counter
                commandsProcessed++;
            }
            else
            {
                // Not enough data for a complete command
                // Buffer contains a partial command, we need more data from client
                // Exit loop and wait for next Socket.Select() iteration to read more
                break;
            }
        }

        // Return total commands processed (useful for logging/metrics)
        return commandsProcessed;
    }

    /// <summary>
    /// Executes a single parsed command by routing it to the appropriate handler.
    ///
    /// Execution Flow:
    /// 1. Extract command name (first element, e.g., "GET", "SET")
    /// 2. Look up handler in registry
    /// 3. If handler found:
    ///    a. Build command context (connection, data store, etc.)
    ///    b. Extract arguments (everything after command name)
    ///    c. Call handler.HandleAsync(context, args)
    ///    d. Handler writes response to connection.WriteBuffer
    /// 4. If handler not found:
    ///    a. Write error response to client
    ///
    /// Command Context:
    /// Handlers need access to multiple services to do their job:
    /// - Connection: To write responses
    /// - DataStore: To read/write key-value data
    /// - ExpirationService: To manage TTLs
    /// - ResponseWriter: To format responses
    ///
    /// We bundle all of these into ICommandContext so handlers have
    /// everything they need without constructor dependencies.
    ///
    /// Example Commands:
    /// - GET key -> cmd = ["GET", "key"], args = ["key"]
    /// - SET key value -> cmd = ["SET", "key", "value"], args = ["key", "value"]
    /// - ZADD myset 1.0 member -> cmd = ["ZADD", "myset", "1.0", "member"], args = ["myset", "1.0", "member"]
    ///
    /// Error Cases:
    /// - Unknown command: Returns "-Unknown cmd\r\n" to client
    /// - Invalid arguments: Handler validates and returns error
    /// - Empty command list: Ignored (shouldn't happen if parser works correctly)
    /// </summary>
    /// <param name="connection">The connection to execute the command on</param>
    /// <param name="cmd">Parsed command as list of strings (includes command name and arguments)</param>
    private async Task ExecuteCommandAsync(Connection connection, List<string> cmd)
    {
        // Empty command list (shouldn't happen, but defensive check)
        if (cmd.Count == 0) return;

        // Extract command name (first element) and convert to uppercase
        // Redis commands are case-insensitive: GET = get = Get
        string commandName = cmd[0].ToUpper();

        // Look up the handler for this command name
        // Returns null if command doesn't exist
        var handler = _commandRegistry.GetHandler(commandName);

        if (handler != null)
        {
            // Command exists, execute it

            // Build the command context with all dependencies the handler needs
            var context = new Services.CommandContext(
                connection,          // To write responses
                _dataStore,          // To read/write data
                _expirationService,  // To manage TTLs
                _responseWriter      // To format responses
            );

            // Extract arguments (everything after the command name)
            // For "SET key value", args = ["key", "value"]
            var args = cmd.Skip(1).ToList();

            // Call the handler to execute the command
            // Handler will write response to connection.WriteBuffer
            await handler.HandleAsync(context, args);
        }
        else
        {
            // Unknown command, return error to client
            // Format: "-Unknown cmd\r\n" (Redis error response format)
            _responseWriter.WriteError(connection.WriteBuffer, 1, "Unknown cmd");
        }
    }
}
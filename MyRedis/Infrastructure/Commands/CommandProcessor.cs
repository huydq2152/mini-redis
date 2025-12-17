using MyRedis.Abstractions.Commands;
using MyRedis.Abstractions.Network;
using MyRedis.Abstractions.Storage;
using MyRedis.Core.Network;
using MyRedis.Services.Commands;

namespace MyRedis.Infrastructure.Commands;

/// <summary>
/// Parses Redis protocol and routes commands to their handlers.
///
/// Responsibility: Protocol Processing and Command Routing
/// - Parse binary Redis protocol from connection buffers
/// - Route commands to registered handlers
/// - Support pipelining (multiple commands in one TCP packet)
/// - Build command execution context for handlers
/// - Enforce fairness via command limiting per iteration
///
/// Protocol Format:
/// MyRedis uses a simplified binary protocol:
/// [4-byte count][4-byte len][string][4-byte len][string]...
///
/// Integration with Event Loop:
/// 1. NetworkServer.ProcessNetworkEvents() reads data into connection buffers
/// 2. RedisServerOrchestrator calls ProcessConnectionDataAsync() for each connection
/// 3. CommandProcessor parses and executes up to MAX_COMMANDS_PER_LOOP commands
/// 4. Responses are written to connection write buffers and flushed
/// 5. If more commands remain, hasMore=true signals orchestrator to resume later
///
/// Pipelining Support:
/// Clients can send multiple commands without waiting for responses:
/// - Client sends: "GET key1\nGET key2\nGET key3" in one packet
/// - Server processes all three commands
/// - Server sends back all three responses
/// - This dramatically improves throughput (batch processing)
///
/// Fairness & Starvation Prevention:
/// To prevent Head-of-Line Blocking (a "greedy" client sending too much command pipelined)
/// Need limit commands processed per iteration:
/// - MAX_COMMANDS_PER_LOOP = 16 commands per connection per loop
/// - After 16 commands, yield to other connections (even if more buffered data)
/// - Return (processed=16, hasMore=true) to signal pending work
/// - Orchestrator adds connection to _resumeList for next iteration
/// - This ensures round-robin fairness: all clients get timeslices
///
/// Command Execution Flow:
/// 1. Parse command from buffer using ProtocolParser
/// 2. Look up handler in CommandRegistry
/// 3. Build ICommandContext with connection, data store, expiration service
/// 4. Call handler.HandleAsync(context, arguments)
/// 5. Handler writes response to connection.Writer
/// 6. Flush response immediately (send to client)
/// 7. Remove parsed bytes from buffer and repeat (up to limit)
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
/// - Command limiting: Prevents starvation, maintains fairness
///
/// Design Pattern: Command Pattern + Strategy Pattern
/// - Command Pattern: Each command is a separate handler object
/// - Strategy Pattern: CommandRegistry selects the right handler
/// - This makes adding new commands trivial (just register a new handler)
/// </summary>
public class CommandProcessor
{
    /// <summary>
    /// Maximum commands to process per connection per event loop iteration.
    ///
    /// Fairness Strategy:
    /// This limit prevents a single connection from monopolizing the server.
    ///
    /// Why 16?
    /// - Redis uses similar limits (processInputBuffer processes in chunks)
    /// - Small enough to ensure fairness (low latency for other clients)
    /// - Large enough to amortize event loop overhead (efficient pipelining)
    /// - Balances low throughput vs latency (avoid starvation)
    /// </summary>
    private const int MaxCommandsPerLoop = 16;
    
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
    /// </summary>
    public CommandProcessor(
        ICommandRegistry commandRegistry,
        IDataStore dataStore,
        IExpirationService expirationService,
        IResponseWriter responseWriter)
    {
        _commandRegistry = commandRegistry ?? throw new ArgumentNullException(nameof(commandRegistry));
        _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        _expirationService = expirationService ?? throw new ArgumentNullException(nameof(expirationService));
        _responseWriter = responseWriter ?? throw new ArgumentNullException(nameof(responseWriter));
    }

    /// <summary>
    /// Processes commands in the connection's read buffer (up to MAX_COMMANDS_PER_LOOP).
    ///
    /// This method is called by RedisServerOrchestrator after NetworkServer detects
    /// that a connection has received data or has pending buffered commands.
    ///
    /// Fairness Implementation:
    /// After processing MAX_COMMANDS_PER_LOOP (16) commands:
    /// - Check if buffer still has data (BytesRead > 0)
    /// - If yes: Return (16, hasMore=true) - connection needs resuming
    /// - If no: Return (16, hasMore=false) - connection is done
    ///
    /// This prevents "Stalled Processing" bug:
    /// - Without hasMore signal, orchestrator would call Select()
    /// - Select() only wakes on NEW network data, not buffered data
    /// - Server would sleep even though buffer has commands to process
    /// - With hasMore=true, orchestrator adds to _resumeList (process next iteration)
    /// 
    /// Performance:
    /// - Zero-copy parsing: ProtocolParser reads directly from buffer
    /// - Immediate response: Flush after each command (low latency)
    /// - Fairness: Command limiting prevents starvation
    /// - Efficient: Minimal event loop overhead
    /// </summary>
    /// <param name="connection">The connection that received data</param>
    /// <returns>Tuple of (commandsProcessed, hasMorePending)</returns>
    public async Task<(int processed, bool hasMore)> ProcessConnectionDataAsync(Connection connection)
    {
        // Track how many commands we process (for metrics and logging)
        var commandsProcessed = 0;

        // Loop to handle pipelining: client may send multiple commands at once
        // Process up to MAX_COMMANDS_PER_LOOP, then yield for fairness
        while (true)
        {
            // FAIRNESS CHECK: Have we processed quota for this iteration?
            if (commandsProcessed >= MaxCommandsPerLoop)
            {
                // Processed fair share (16 commands)
                // Check if there's likely more data to process
                // (even a partial command indicates we should resume later)
                var hasMoreData = connection.BytesRead > 0;

                if (hasMoreData)
                {
                    // There's still data in the buffer
                    // Yield to other connections for fairness
                    // Orchestrator will add us to _resumeList
                    Console.WriteLine($"[Fairness] Yielding after {commandsProcessed} commands (buffer has {connection.BytesRead} bytes remaining)");
                    return (commandsProcessed, hasMore: true);
                }

                // Processed exactly 16 commands and buffer is empty
                // This is the normal case: buffer fully drained
                return (commandsProcessed, hasMore: false);
            }

            // Try to parse one command from the buffer
            // TryParse returns:
            // - true: Successfully parsed a command (cmd) and consumed bytes
            // - false: Not enough data for a complete command (need more from client)
            if (ProtocolParser.TryParse(connection.ReadBuffer, connection.BytesRead,
                out var cmd, out var consumed))
            {
                // Successfully parsed one complete command
                // cmd is a list of strings: ["GET", "key"] or ["SET", "key", "value"]
                if (cmd != null)
                {
                    Console.WriteLine($"[Command] {string.Join(" ", cmd)}");

                    // Check if previous command has unsent data (partial send)
                    // If so, we CANNOT process a new command yet - we must wait for the
                    // pending write to complete first to avoid corrupting the response stream
                    if (connection.WriteBufferOffset > 0 && connection.WriteBufferOffset < connection.WrittenCount)
                    {
                        // Previous command's response hasn't been fully sent yet
                        // This should be rare (only happens with large responses or slow clients)
                        // We MUST NOT execute the new command because:
                        // 1. New response would corrupt the unsent portion of previous response
                        // 2. Response order would be violated (pipelining requires in-order responses)
                        //
                        // Solution: Skip this command for now, it will be processed in the next
                        // event loop iteration after the pending write completes
                        Console.WriteLine($"[WARNING] Skipping command due to pending write: {string.Join(" ", cmd)}");

                        // Buffer has a complete command, but we can't process it yet
                        // Return hasMore=true so orchestrator resumes us when write completes
                        return (commandsProcessed, hasMore: true);
                    }

                    // Reset write buffer BEFORE executing command
                    // This ensures each command starts with a clean slate
                    // Without this, previous error responses can corrupt new responses
                    connection.ResetWriteBuffer();

                    // Execute the command (call the appropriate handler)
                    await ExecuteCommandAsync(connection, cmd);
                }

                // Send response immediately to the client
                // This ensures low latency (don't buffer responses)
                // Flush() returns:
                // - true: All data sent (normal case for small responses)
                // - false: Partial send, needs write monitoring (large responses or slow clients)
                var allSent = connection.Flush();

                // Remove processed bytes from the buffer
                // This compacts the buffer so remaining data moves to the front
                // If buffer had [cmd1][cmd2][partial], after shift: [cmd2][partial]
                connection.ShiftBuffer(consumed);

                // Increment counter
                commandsProcessed++;

                // If partial send, yield immediately to allow write completion
                if (!allSent)
                {
                    // Still have unsent data in write buffer
                    // Orchestrator will resume us when write is ready
                    return (commandsProcessed, hasMore: true);
                }
            }
            else
            {
                // Not enough data for a complete command
                // Buffer contains a partial command, we need more data from client
                // Exit loop and wait for next Socket.Select() iteration to read more
                return (commandsProcessed, hasMore: false);
            }
        }
    }

    /// <summary>
    /// Executes a single parsed command by routing it to the appropriate handler.
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
        var commandName = cmd[0].ToUpper();

        // Look up the handler for this command name
        // Returns null if command doesn't exist
        var handler = _commandRegistry.GetHandler(commandName);

        if (handler != null)
        {
            // Build the command context with all dependencies the handler needs
            var context = new CommandContext(
                connection,          // To write responses
                _dataStore,          // To read/write data
                _expirationService,  // To manage TTLs
                _responseWriter      // To format responses
            );

            // Extract arguments (everything after the command name)
            var args = cmd.Skip(1).ToList();

            // Call the handler to execute the command
            // Handler will write response to connection.Writer
            await handler.HandleAsync(context, args);
        }
        else
        {
            // Unknown command, return error to client
            // Format: "-Unknown cmd\r\n" (Redis error response format)
            _responseWriter.WriteError(connection.Writer, 1, "Unknown cmd");
        }
    }
}
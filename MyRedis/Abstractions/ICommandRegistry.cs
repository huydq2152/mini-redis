namespace MyRedis.Abstractions;

/// <summary>
/// Registry for command handlers that maps command names to their implementations.
///
/// Design Pattern: Registry Pattern + Command Pattern
/// - Decouples command parsing from command execution
/// - Makes it easy to add new commands without modifying core infrastructure
/// - Enables runtime command discovery and introspection
///
/// How Commands Are Registered:
/// 1. Each command handler implements ICommandHandler
/// 2. Handler specifies its command name (e.g., "GET", "SET", "ZADD")
/// 3. Factory calls Register() for each handler during startup
/// 4. CommandProcessor looks up handlers by name when executing commands
///
/// Example:
/// - Client sends: "GET mykey"
/// - Parser extracts: ["GET", "mykey"]
/// - Registry returns: GetCommandHandler instance
/// - Handler executes with args: ["mykey"]
///
/// Implementation: CommandRegistry uses a case-insensitive dictionary for O(1) lookups.
/// </summary>
public interface ICommandRegistry
{
    /// <summary>
    /// Registers a command handler for a specific Redis command.
    ///
    /// The handler's CommandName property determines which command it handles.
    /// Command names are case-insensitive (GET, get, GeT are all the same).
    ///
    /// This is typically called during server initialization in RedisServerFactory.
    /// Each handler is registered exactly once as a singleton.
    /// </summary>
    /// <param name="handler">The command handler to register</param>
    /// <exception cref="ArgumentException">If a handler for this command is already registered</exception>
    void Register(ICommandHandler handler);

    /// <summary>
    /// Gets the command handler for a specific command name.
    ///
    /// This is called by CommandProcessor for every incoming command to determine
    /// which handler should process it.
    ///
    /// Lookup is case-insensitive and O(1).
    /// </summary>
    /// <param name="commandName">The command name (e.g., "GET", "SET", "ZADD")</param>
    /// <returns>The handler if found, null if the command is not recognized</returns>
    ICommandHandler? GetHandler(string commandName);

    /// <summary>
    /// Gets all registered command handlers.
    ///
    /// Useful for:
    /// - Debugging: Seeing which commands are available
    /// - Introspection: Building help text or command lists
    /// - Testing: Verifying all expected handlers are registered
    /// </summary>
    /// <returns>Collection of all registered command handlers</returns>
    IEnumerable<ICommandHandler> GetAllHandlers();
}
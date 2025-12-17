using MyRedis.Abstractions.Commands;

namespace MyRedis.Services.Commands;

/// <summary>
/// Registry implementation that manages the mapping between command names
/// and their corresponding command handler implementations.
///
/// Design Pattern: Registry Pattern + Factory Pattern
/// This class serves as a central registry where all command handlers are registered
/// during server initialization, and later used to look up handlers during command execution.
/// It decouples command parsing from command execution, making the system extensible.
///
/// Usage Flow:
/// 1. Server Startup: RedisServerFactory registers all command handlers
/// 2. Client Request: CommandProcessor extracts command name from protocol
/// 3. Handler Lookup: CommandProcessor calls GetHandler() to find appropriate handler
/// 4. Command Execution: Handler is invoked with command arguments and context
///
/// How Commands Are Registered:
/// 1. Each command handler implements ICommandHandler
/// 2. Handler specifies its command name (e.g., "GET", "SET", "ZADD")
/// 3. Factory calls Register() for each handler during startup
/// 4. CommandProcessor looks up handlers by name when executing commands
///
/// Extensibility:
/// New commands can be added by:
/// 1. Creating a new class implementing ICommandHandler
/// 2. Registering it in this registry during server startup
/// 3. No changes needed to core command processing logic
/// </summary>
public class CommandRegistry : ICommandRegistry
{
    /// <summary>
    /// Internal dictionary storing the mapping from command names to their handlers.
    /// Uses case-insensitive comparison to match protocol behavior where
    /// command names are case-insensitive (e.g., "GET", "get", "GeT" all map to same command).
    /// </summary>
    private readonly Dictionary<string, ICommandHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a command handler for its associated command.
    /// The handler's CommandName property determines which command it will handle.
    /// If a handler for the same command already exists, it will be replaced.
    /// </summary>
    /// <param name="handler">The command handler to register</param>
    /// <exception cref="ArgumentNullException">Thrown when handler is null</exception>
    public void Register(ICommandHandler handler)
    {
        if (handler == null) 
            throw new ArgumentNullException(nameof(handler));

        // Register using the handler's command name as the key
        // Dictionary uses case-insensitive comparer, so "GET", "get", "GeT" map to same handler
        _handlers[handler.CommandName] = handler;
    }

    /// <summary>
    /// Retrieves the command handler for the specified command name.
    /// Performs case-insensitive lookup to match Redis protocol behavior.
    /// This method is called for every incoming client command.
    /// </summary>
    /// <param name="commandName">The Redis command name (e.g., "GET", "SET", "ZADD")</param>
    /// <returns>
    /// The registered command handler if found, null if the command is not recognized.
    /// Null return indicates an unknown command, which should result in an error response.
    /// </returns>
    public ICommandHandler? GetHandler(string commandName)
    {
        return _handlers.GetValueOrDefault(commandName);
    }

    /// <summary>
    /// Returns all registered command handlers in the registry.
    /// Useful for debugging, introspection, and building command lists for clients.
    /// </summary>
    /// <returns>
    /// A collection containing all registered command handlers.
    /// The order is not guaranteed and may vary between calls.
    /// </returns>
    /// <remarks>
    /// This method is primarily used for:
    /// - Debugging: Verifying which commands are available
    /// - Monitoring: Reporting server capabilities
    /// - Testing: Ensuring all expected handlers are registered
    /// - Administration: Building help text or command documentation
    /// </remarks>
    public IEnumerable<ICommandHandler> GetAllHandlers()
    {
        return _handlers.Values;
    }
}
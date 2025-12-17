namespace MyRedis.Abstractions.Commands;

/// <summary>
/// Registry for command handlers that maps command names to their implementations.
/// </summary>
public interface ICommandRegistry
{
    /// <summary>
    /// Registers a command handler for a specific Redis command.
    /// </summary>
    /// <param name="handler">The command handler to register</param>
    /// <exception cref="ArgumentException">If a handler for this command is already registered</exception>
    void Register(ICommandHandler handler);

    /// <summary>
    /// Gets the command handler for a specific command name.
    /// </summary>
    /// <param name="commandName">The command name (e.g., "GET", "SET", "ZADD")</param>
    /// <returns>The handler if found, null if the command is not recognized</returns>
    ICommandHandler? GetHandler(string commandName);

    /// <summary>
    /// Gets all registered command handlers.
    /// </summary>
    /// <returns>Collection of all registered command handlers</returns>
    IEnumerable<ICommandHandler> GetAllHandlers();
}
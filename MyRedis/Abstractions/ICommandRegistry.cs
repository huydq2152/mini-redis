namespace MyRedis.Abstractions;

/// <summary>
/// Registry for command handlers
/// </summary>
public interface ICommandRegistry
{
    /// <summary>
    /// Registers a command handler
    /// </summary>
    /// <param name="handler">The command handler to register</param>
    void Register(ICommandHandler handler);

    /// <summary>
    /// Gets a command handler by command name
    /// </summary>
    /// <param name="commandName">The command name</param>
    /// <returns>The handler if found, null otherwise</returns>
    ICommandHandler? GetHandler(string commandName);

    /// <summary>
    /// Gets all registered handlers
    /// </summary>
    /// <returns>Collection of all registered handlers</returns>
    IEnumerable<ICommandHandler> GetAllHandlers();
}
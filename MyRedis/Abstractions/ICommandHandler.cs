namespace MyRedis.Abstractions;

/// <summary>
/// Represents a command handler that can execute Redis commands
/// </summary>
public interface ICommandHandler
{
    /// <summary>
    /// Gets the command name that this handler can process
    /// </summary>
    string CommandName { get; }

    /// <summary>
    /// Executes the command with the given parameters
    /// </summary>
    /// <param name="context">The execution context</param>
    /// <param name="args">Command arguments (excluding the command name)</param>
    /// <returns>True if the command was handled successfully</returns>
    Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args);
}
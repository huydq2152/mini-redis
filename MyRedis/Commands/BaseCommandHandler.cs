using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Base class for command handlers providing common functionality and error handling.
/// This abstract class implements the ICommandHandler interface and provides shared
/// error handling methods for all Redis command implementations.
/// </summary>
public abstract class BaseCommandHandler : ICommandHandler
{
    /// <summary>
    /// Gets the name of the Redis command that this handler processes.
    /// This property must be implemented by each concrete command handler.
    /// </summary>
    public abstract string CommandName { get; }

    /// <summary>
    /// Handles the execution of a Redis command asynchronously.
    /// This method must be implemented by each concrete command handler to define
    /// the specific behavior for processing the command.
    /// </summary>
    /// <param name="context">The command execution context containing connection, data store, and response writer</param>
    /// <param name="args">The command arguments passed by the client</param>
    /// <returns>A task that returns true if the command was handled successfully, false otherwise</returns>
    public abstract Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args);

    /// <summary>
    /// Writes a generic error message to the client using the Redis error response format.
    /// This helper method standardizes error responses across all command handlers.
    /// </summary>
    /// <param name="context">The command execution context</param>
    /// <param name="message">The error message to send to the client</param>
    protected void WriteError(ICommandContext context, string message)
    {
        context.ResponseWriter.WriteError(context.Connection.WriteBuffer, 1, message);
    }

    /// <summary>
    /// Writes a "wrong number of arguments" error to the client.
    /// This is a common error type in Redis when commands receive incorrect argument counts.
    /// </summary>
    /// <param name="context">The command execution context</param>
    protected void WriteWrongArgsError(ICommandContext context)
    {
        WriteError(context, "ERR wrong number of arguments");
    }

    /// <summary>
    /// Writes a "wrong data type" error to the client.
    /// This error occurs when an operation is attempted on a key that holds
    /// an incompatible data type (e.g., trying to use list operations on a string).
    /// </summary>
    /// <param name="context">The command execution context</param>
    protected void WriteWrongTypeError(ICommandContext context)
    {
        WriteError(context, "WRONGTYPE Operation against a key holding the wrong kind of value");
    }
}
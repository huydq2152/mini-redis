using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the ECHO command which returns the given string back to the client.
/// This command is primarily used for testing connectivity and verifying that
/// the Redis server is responding correctly to commands.
/// </summary>
public class EchoCommandHandler : BaseCommandHandler
{
    /// <summary>
    /// Gets the Redis command name that this handler processes.
    /// </summary>
    public override string CommandName => "ECHO";

    /// <summary>
    /// Handles the ECHO command execution by returning the provided message back to the client.
    /// This is a simple diagnostic command that echoes the first argument back as a response.
    /// </summary>
    /// <param name="context">The command execution context</param>
    /// <param name="args">Command arguments - expects at least one argument (the message to echo)</param>
    /// <returns>A task that completes when the command has been processed</returns>
    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // Check if at least one argument was provided
        if (args.Count > 0)
        {
            // Echo the first argument back to the client as a string response
            context.ResponseWriter.WriteString(context.Connection.Writer, args[0]);
        }
        else
        {
            // No arguments provided - return an error
            WriteError(context, "Missing arg");
        }

        return Task.FromResult(true);
    }
}
using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the PING command which tests connectivity to the Redis server.
/// This command is commonly used for health checks and verifying that the
/// server is alive and responding to requests. Always responds with "PONG".
/// </summary>
public class PingCommandHandler : BaseCommandHandler
{
    /// <summary>
    /// Gets the Redis command name that this handler processes.
    /// </summary>
    public override string CommandName => "PING";

    /// <summary>
    /// Handles the PING command execution by responding with "PONG".
    /// This is a simple connectivity test that requires no arguments and
    /// always returns the same response, confirming the server is operational.
    /// </summary>
    /// <param name="context">The command execution context</param>
    /// <param name="args">Command arguments - PING typically requires no arguments</param>
    /// <returns>A task that completes when the command has been processed</returns>
    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // Respond with the standard "PONG" message to confirm server connectivity
        // This is the expected response for the PING command in Redis protocol
        context.ResponseWriter.WriteString(context.Connection.WriteBuffer, "PONG");
        return Task.FromResult(true);
    }
}
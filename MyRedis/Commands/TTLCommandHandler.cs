using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the TTL (Time To Live) command which returns the remaining time
/// until a key expires. This command is essential for monitoring key expiration
/// and implementing time-based cleanup strategies.
/// </summary>
public class TtlCommandHandler : BaseCommandHandler
{
    /// <summary>
    /// Gets the Redis command name that this handler processes.
    /// </summary>
    public override string CommandName => "TTL";

    /// <summary>
    /// Handles the TTL command execution by returning the remaining time in seconds
    /// until the specified key expires. Returns special values for different states:
    /// -2: Key does not exist
    /// -1: Key exists but has no expiration set (persistent key)
    /// Positive integer: Remaining seconds until expiration
    /// </summary>
    /// <param name="context">The command execution context</param>
    /// <param name="args">Command arguments - expects exactly one argument (the key to check)</param>
    /// <returns>A task that completes when the command has been processed</returns>
    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // Validate command syntax: TTL requires exactly one argument (the key)
        if (args.Count != 1)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        string key = args[0];
        
        // Check if the key exists in the data store
        if (!context.DataStore.Exists(key))
        {
            // Key does not exist - return -2 as per Redis specification
            context.ResponseWriter.WriteInt(context.Connection.WriteBuffer, -2);
            return Task.FromResult(true);
        }

        // Get the remaining time to live for the key from expiration service
        long? ttl = context.ExpirationService.GetTtl(key);
        
        if (ttl == null)
        {
            // Key exists but has no expiration set (persistent key) - return -1
            context.ResponseWriter.WriteInt(context.Connection.WriteBuffer, -1);
        }
        else
        {
            // Key has expiration set - return remaining time in seconds
            // Convert from milliseconds (internal storage) to seconds (Redis standard)
            context.ResponseWriter.WriteInt(context.Connection.WriteBuffer, ttl.Value / 1000);
        }

        return Task.FromResult(true);
    }
}
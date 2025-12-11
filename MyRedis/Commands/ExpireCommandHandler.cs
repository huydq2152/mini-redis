using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the EXPIRE command which sets a timeout on a key.
/// After the timeout expires, the key will automatically be deleted.
/// This implements Redis key expiration functionality for automatic cleanup.
/// </summary>
public class ExpireCommandHandler : BaseCommandHandler
{
    /// <summary>
    /// Gets the Redis command name that this handler processes.
    /// </summary>
    public override string CommandName => "EXPIRE";

    /// <summary>
    /// Handles the EXPIRE command execution by setting an expiration time on the specified key.
    /// The key will be automatically deleted after the specified number of seconds.
    /// </summary>
    /// <param name="context">The command execution context</param>
    /// <param name="args">Command arguments - expects exactly two arguments: key and timeout in seconds</param>
    /// <returns>A task that completes when the command has been processed</returns>
    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // Validate command syntax: EXPIRE requires exactly two arguments (key and seconds)
        if (args.Count != 2)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        string key = args[0];
        
        // Parse the timeout value - must be a valid integer representing seconds
        if (!int.TryParse(args[1], out int seconds))
        {
            WriteError(context, "ERR value is not an integer");
            return Task.FromResult(true);
        }

        // Check if the key exists in the data store
        if (context.DataStore.Exists(key))
        {
            // Set expiration time (convert seconds to milliseconds for internal storage)
            // The expiration service handles the actual cleanup when the timeout is reached
            context.ExpirationService.SetExpiration(key, seconds * 1000);
            
            // Return 1 to indicate the timeout was successfully set
            context.ResponseWriter.WriteInt(context.Connection.Writer, 1);
        }
        else
        {
            // Key doesn't exist - cannot set expiration on non-existent key
            // Return 0 to indicate no expiration was set
            context.ResponseWriter.WriteInt(context.Connection.Writer, 0);
        }

        return Task.FromResult(true);
    }
}
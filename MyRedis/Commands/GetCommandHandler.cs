using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the GET command which retrieves the string value of a key.
/// This is one of the most fundamental Redis commands for retrieving stored data.
/// Implements lazy expiration checking to ensure expired keys are properly handled.
/// </summary>
public class GetCommandHandler : BaseCommandHandler
{
    /// <summary>
    /// Gets the Redis command name that this handler processes.
    /// </summary>
    public override string CommandName => "GET";

    /// <summary>
    /// Handles the GET command execution by retrieving the value associated with the specified key.
    /// Performs lazy expiration checking to ensure expired keys are cleaned up on access.
    /// </summary>
    /// <param name="context">The command execution context</param>
    /// <param name="args">Command arguments - expects exactly one argument (the key to retrieve)</param>
    /// <returns>A task that completes when the command has been processed</returns>
    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // Validate command syntax: GET requires exactly one argument (the key)
        if (args.Count != 1)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        string key = args[0];

        // Implement lazy expiration: check if the key has expired and clean it up
        // This ensures that expired keys are removed when accessed, maintaining data consistency
        if (context.ExpirationService.IsExpired(key))
        {
            context.DataStore.Remove(key);
        }

        // Attempt to retrieve the value as a string from the data store
        var value = context.DataStore.Get<string>(key);
        
        if (value != null)
        {
            // Key exists and has a value - return it as a string response
            context.ResponseWriter.WriteString(context.Connection.WriteBuffer, value);
        }
        else
        {
            // Key doesn't exist or was expired - return Redis nil response
            context.ResponseWriter.WriteNil(context.Connection.WriteBuffer);
        }

        return Task.FromResult(true);
    }
}
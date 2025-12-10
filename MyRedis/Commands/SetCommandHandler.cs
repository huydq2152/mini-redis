using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the SET command which stores a string value at the specified key.
/// This is one of the most fundamental Redis commands for storing data.
/// If the key already exists, its value will be overwritten regardless of type.
/// </summary>
public class SetCommandHandler : BaseCommandHandler
{
    /// <summary>
    /// Gets the Redis command name that this handler processes.
    /// </summary>
    public override string CommandName => "SET";

    /// <summary>
    /// Handles the SET command execution by storing the specified value at the given key.
    /// This operation will overwrite any existing value at the key, regardless of its type.
    /// </summary>
    /// <param name="context">The command execution context</param>
    /// <param name="args">Command arguments - expects exactly two arguments: key and value</param>
    /// <returns>A task that completes when the command has been processed</returns>
    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // Validate command syntax: SET requires exactly two arguments (key and value)
        if (args.Count != 2)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        string key = args[0];
        string value = args[1];

        // Store the key-value pair in the data store
        // This will overwrite any existing value at this key
        context.DataStore.Set(key, value);
        
        // Redis SET command typically returns "OK" but this implementation uses nil
        // Both are valid Redis responses for successful SET operations
        context.ResponseWriter.WriteNil(context.Connection.WriteBuffer);

        return Task.FromResult(true);
    }
}
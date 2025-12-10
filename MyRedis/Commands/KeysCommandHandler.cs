using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the KEYS command which returns all keys matching a given pattern.
/// In this simplified implementation, it returns all keys in the database.
/// Note: In production Redis, this command should be used carefully as it can
/// block the server when there are many keys in the database.
/// </summary>
public class KeysCommandHandler : BaseCommandHandler
{
    /// <summary>
    /// Gets the Redis command name that this handler processes.
    /// </summary>
    public override string CommandName => "KEYS";

    /// <summary>
    /// Handles the KEYS command execution by returning all keys currently stored in the database.
    /// This simplified implementation returns all keys without pattern matching.
    /// In a production environment, pattern matching and pagination should be implemented.
    /// </summary>
    /// <param name="context">The command execution context</param>
    /// <param name="args">Command arguments - in full Redis implementation, would include pattern matching</param>
    /// <returns>A task that completes when the command has been processed</returns>
    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // Retrieve all keys from the data store
        // Note: This operation can be expensive with large datasets
        // Production implementations should consider memory usage and blocking time
        var keys = context.DataStore.GetAllKeys().ToList();
        
        // Write the response as a Redis array containing all key names
        // First, write the array header with the number of keys
        context.ResponseWriter.WriteArrayHeader(context.Connection.WriteBuffer, keys.Count);
        
        // Then write each key as a string element in the array
        foreach (var key in keys)
        {
            context.ResponseWriter.WriteString(context.Connection.WriteBuffer, key);
        }

        return Task.FromResult(true);
    }
}
using MyRedis.Abstractions;
using MyRedis.Storage.DataStructures;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the ZADD command which adds members to a sorted set with associated scores.
/// Sorted sets (ZSets) are Redis data structures that maintain elements in sorted order
/// by their numeric scores, allowing for efficient range queries and ranking operations.
/// </summary>
public class ZAddCommandHandler : BaseCommandHandler
{
    /// <summary>
    /// Gets the Redis command name that this handler processes.
    /// </summary>
    public override string CommandName => "ZADD";

    /// <summary>
    /// Handles the ZADD command execution by adding a member with a score to a sorted set.
    /// If the key doesn't exist, a new sorted set is created. If the member already exists,
    /// its score is updated. The command returns the number of new elements added.
    /// </summary>
    /// <param name="context">The command execution context</param>
    /// <param name="args">Command arguments - expects exactly three arguments: key, score, and member</param>
    /// <returns>A task that completes when the command has been processed</returns>
    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // Validate command syntax: ZADD key score member
        if (args.Count != 3)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        string key = args[0];
        
        // Parse the score - must be a valid floating-point number
        if (!double.TryParse(args[1], out double score))
        {
            WriteError(context, "ERR value is not a float");
            return Task.FromResult(true);
        }

        string member = args[2];

        // Get existing sorted set or create a new one if the key doesn't exist
        var value = context.DataStore.Get(key);
        if (value == null)
        {
            // Key doesn't exist - create a new sorted set
            value = new SortedSet();
            context.DataStore.Set(key, value);
        }

        // Verify the value is actually a sorted set (type safety)
        if (value is SortedSet zset)
        {
            // Add the member with its score to the sorted set
            // The Add method returns true if this is a new member, false if updating existing
            bool added = zset.Add(member, score);
            
            // Return 1 if a new element was added, 0 if an existing element was updated
            context.ResponseWriter.WriteInt(context.Connection.Writer, added ? 1 : 0);
        }
        else
        {
            // Key exists but holds a different data type - return type error
            WriteWrongTypeError(context);
        }

        return Task.FromResult(true);
    }
}
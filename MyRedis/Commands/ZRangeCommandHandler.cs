using MyRedis.Abstractions;
using MyRedis.Storage.DataStructures;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the ZRANGE command which returns a range of members from a sorted set
/// by their index positions. Members are returned in ascending order by score.
/// This command is essential for pagination and retrieving ordered subsets of data.
/// </summary>
public class ZRangeCommandHandler : BaseCommandHandler
{
    /// <summary>
    /// Gets the Redis command name that this handler processes.
    /// </summary>
    public override string CommandName => "ZRANGE";

    /// <summary>
    /// Handles the ZRANGE command execution by returning a range of members from a sorted set.
    /// The range is specified by start and stop indices (0-based). Negative indices count
    /// from the end of the set. Returns members in ascending order by score.
    /// </summary>
    /// <param name="context">The command execution context</param>
    /// <param name="args">Command arguments - expects exactly three arguments: key, start index, and stop index</param>
    /// <returns>A task that completes when the command has been processed</returns>
    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // Validate command syntax: ZRANGE key start stop
        if (args.Count != 3)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        string key = args[0];
        
        // Parse start and stop indices - both must be valid integers
        if (!int.TryParse(args[1], out int start) || !int.TryParse(args[2], out int stop))
        {
            WriteError(context, "ERR value is not an integer");
            return Task.FromResult(true);
        }

        var value = context.DataStore.Get(key);
        if (value != null)
        {
            // Verify the value is actually a sorted set (type safety)
            if (value is SortedSet zset)
            {
                // Get the range of members from the sorted set
                // The Range method handles index bounds and returns members in score order
                var items = zset.Range(start, stop);

                // Send the response as a Redis array containing the member names
                // First write the array header with the number of items
                context.ResponseWriter.WriteArrayHeader(context.Connection.WriteBuffer, items.Count);
                
                // Then write each member as a string element in the array
                foreach (var item in items)
                {
                    context.ResponseWriter.WriteString(context.Connection.WriteBuffer, item);
                }
            }
            else
            {
                // Key exists but holds a different data type - return type error
                WriteWrongTypeError(context);
            }
        }
        else
        {
            // Key doesn't exist - return an empty array as per Redis specification
            context.ResponseWriter.WriteArrayHeader(context.Connection.WriteBuffer, 0);
        }

        return Task.FromResult(true);
    }
}
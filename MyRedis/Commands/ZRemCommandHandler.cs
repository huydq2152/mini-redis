using MyRedis.Abstractions;
using MyRedis.Storage.DataStructures;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the ZREM command which removes one or more members from a sorted set.
/// Returns the number of members that were actually removed (not including non-existing members).
/// This command is essential for managing sorted set membership.
/// </summary>
public class ZRemCommandHandler : BaseCommandHandler
{
    /// <summary>
    /// Gets the Redis command name that this handler processes.
    /// </summary>
    public override string CommandName => "ZREM";

    /// <summary>
    /// Handles the ZREM command execution by removing one or more members from a sorted set.
    /// Returns the count of members that were successfully removed.
    /// </summary>
    /// <param name="context">The command execution context</param>
    /// <param name="args">Command arguments - expects at least two arguments: key and one or more member names</param>
    /// <returns>A task that completes when the command has been processed</returns>
    /// <remarks>
    /// Redis ZREM Syntax: ZREM key member [member ...]
    ///
    /// Behavior:
    /// - Removes specified members from the sorted set stored at key
    /// - Non-existing members are ignored
    /// - Returns the number of members removed (not including non-existing members)
    /// - If key doesn't exist, returns 0
    /// - If key exists but is not a sorted set, returns a type error
    ///
    /// Examples:
    /// ZREM myzset "one" -> :1 (if "one" existed)
    /// ZREM myzset "one" "two" -> :2 (if both existed)
    /// ZREM myzset "nonexistent" -> :0
    /// ZREM nonexistentkey "member" -> :0
    /// </remarks>
    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // Validate command syntax: ZREM key member [member ...]
        if (args.Count < 2)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        string key = args[0];
        int removedCount = 0;

        var value = context.DataStore.Get(key);
        if (value != null)
        {
            // Verify the value is actually a sorted set (type safety)
            if (value is SortedSet zset)
            {
                // Remove each member and count successful removals
                // Start from index 1 (skip the key at index 0)
                for (int i = 1; i < args.Count; i++)
                {
                    string member = args[i];
                    if (zset.Remove(member))
                    {
                        removedCount++;
                    }
                }
            }
            else
            {
                // Key exists but holds a different data type - return type error
                WriteWrongTypeError(context);
                return Task.FromResult(true);
            }
        }
        // If key doesn't exist, removedCount stays 0 (Redis behavior)

        // Return the count of removed members as an integer
        context.ResponseWriter.WriteInt(context.Connection.Writer, removedCount);

        return Task.FromResult(true);
    }
}

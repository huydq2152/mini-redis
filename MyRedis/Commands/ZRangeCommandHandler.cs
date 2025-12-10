using MyRedis.Abstractions;
using MyRedis.Storage.DataStructures;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the ZRANGE command
/// </summary>
public class ZRangeCommandHandler : BaseCommandHandler
{
    public override string CommandName => "ZRANGE";

    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // Syntax: ZRANGE key start stop
        if (args.Count != 3)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        string key = args[0];
        if (!int.TryParse(args[1], out int start) || !int.TryParse(args[2], out int stop))
        {
            WriteError(context, "ERR value is not an integer");
            return Task.FromResult(true);
        }

        var value = context.DataStore.Get(key);
        if (value != null)
        {
            if (value is SortedSet zset)
            {
                var items = zset.Range(start, stop);

                // Serialize string array response
                context.ResponseWriter.WriteArrayHeader(context.Connection.WriteBuffer, items.Count);
                foreach (var item in items)
                {
                    context.ResponseWriter.WriteString(context.Connection.WriteBuffer, item);
                }
            }
            else
            {
                WriteWrongTypeError(context);
            }
        }
        else
        {
            // Key doesn't exist -> Return empty array
            context.ResponseWriter.WriteArrayHeader(context.Connection.WriteBuffer, 0);
        }

        return Task.FromResult(true);
    }
}
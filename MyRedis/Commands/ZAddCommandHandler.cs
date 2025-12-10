using MyRedis.Abstractions;
using MyRedis.Storage.DataStructures;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the ZADD command
/// </summary>
public class ZAddCommandHandler : BaseCommandHandler
{
    public override string CommandName => "ZADD";

    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // Syntax: ZADD key score member
        if (args.Count != 3)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        string key = args[0];
        if (!double.TryParse(args[1], out double score))
        {
            WriteError(context, "ERR value is not a float");
            return Task.FromResult(true);
        }

        string member = args[2];

        // Get or create ZSet
        var value = context.DataStore.Get(key);
        if (value == null)
        {
            value = new SortedSet();
            context.DataStore.Set(key, value);
        }

        if (value is SortedSet zset)
        {
            bool added = zset.Add(member, score);
            context.ResponseWriter.WriteInt(context.Connection.WriteBuffer, added ? 1 : 0);
        }
        else
        {
            WriteWrongTypeError(context);
        }

        return Task.FromResult(true);
    }
}
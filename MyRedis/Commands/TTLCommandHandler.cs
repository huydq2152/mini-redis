using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the TTL command
/// </summary>
public class TtlCommandHandler : BaseCommandHandler
{
    public override string CommandName => "TTL";

    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 1)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        string key = args[0];
        if (!context.DataStore.Exists(key))
        {
            context.ResponseWriter.WriteInt(context.Connection.WriteBuffer, -2); // Key not exists
            return Task.FromResult(true);
        }

        long? ttl = context.ExpirationService.GetTtl(key);
        if (ttl == null)
        {
            context.ResponseWriter.WriteInt(context.Connection.WriteBuffer, -1); // No TTL
        }
        else
        {
            context.ResponseWriter.WriteInt(context.Connection.WriteBuffer, ttl.Value / 1000); // Return seconds
        }

        return Task.FromResult(true);
    }
}
using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the EXPIRE command
/// </summary>
public class ExpireCommandHandler : BaseCommandHandler
{
    public override string CommandName => "EXPIRE";

    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 2)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        string key = args[0];
        if (!int.TryParse(args[1], out int seconds))
        {
            WriteError(context, "ERR value is not an integer");
            return Task.FromResult(true);
        }

        if (context.DataStore.Exists(key))
        {
            context.ExpirationService.SetExpiration(key, seconds * 1000);
            context.ResponseWriter.WriteInt(context.Connection.WriteBuffer, 1);
        }
        else
        {
            context.ResponseWriter.WriteInt(context.Connection.WriteBuffer, 0);
        }

        return Task.FromResult(true);
    }
}
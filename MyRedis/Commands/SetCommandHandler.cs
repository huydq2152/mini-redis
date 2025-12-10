using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the SET command
/// </summary>
public class SetCommandHandler : BaseCommandHandler
{
    public override string CommandName => "SET";

    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 2)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        string key = args[0];
        string value = args[1];

        context.DataStore.Set(key, value);
        context.ResponseWriter.WriteNil(context.Connection.WriteBuffer);

        return Task.FromResult(true);
    }
}
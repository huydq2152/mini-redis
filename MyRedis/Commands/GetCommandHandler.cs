using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the GET command
/// </summary>
public class GetCommandHandler : BaseCommandHandler
{
    public override string CommandName => "GET";

    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 1)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        string key = args[0];

        // Check for lazy expiration
        if (context.ExpirationService.IsExpired(key))
        {
            context.DataStore.Remove(key);
        }

        var value = context.DataStore.Get<string>(key);
        if (value != null)
        {
            context.ResponseWriter.WriteString(context.Connection.WriteBuffer, value);
        }
        else
        {
            context.ResponseWriter.WriteNil(context.Connection.WriteBuffer);
        }

        return Task.FromResult(true);
    }
}
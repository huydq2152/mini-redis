using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the KEYS command
/// </summary>
public class KeysCommandHandler : BaseCommandHandler
{
    public override string CommandName => "KEYS";

    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        var keys = context.DataStore.GetAllKeys().ToList();
        
        context.ResponseWriter.WriteArrayHeader(context.Connection.WriteBuffer, keys.Count);
        foreach (var key in keys)
        {
            context.ResponseWriter.WriteString(context.Connection.WriteBuffer, key);
        }

        return Task.FromResult(true);
    }
}
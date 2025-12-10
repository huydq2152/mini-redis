using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the ECHO command
/// </summary>
public class EchoCommandHandler : BaseCommandHandler
{
    public override string CommandName => "ECHO";

    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        if (args.Count > 0)
        {
            context.ResponseWriter.WriteString(context.Connection.WriteBuffer, args[0]);
        }
        else
        {
            WriteError(context, "Missing arg");
        }

        return Task.FromResult(true);
    }
}
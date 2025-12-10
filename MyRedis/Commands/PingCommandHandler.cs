using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the PING command
/// </summary>
public class PingCommandHandler : BaseCommandHandler
{
    public override string CommandName => "PING";

    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        context.ResponseWriter.WriteString(context.Connection.WriteBuffer, "PONG");
        return Task.FromResult(true);
    }
}
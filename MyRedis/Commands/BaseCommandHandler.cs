using MyRedis.Abstractions;

namespace MyRedis.Commands;

/// <summary>
/// Base class for command handlers providing common functionality
/// </summary>
public abstract class BaseCommandHandler : ICommandHandler
{
    public abstract string CommandName { get; }

    public abstract Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args);

    protected void WriteError(ICommandContext context, string message)
    {
        context.ResponseWriter.WriteError(context.Connection.WriteBuffer, 1, message);
    }

    protected void WriteWrongArgsError(ICommandContext context)
    {
        WriteError(context, "ERR wrong number of arguments");
    }

    protected void WriteWrongTypeError(ICommandContext context)
    {
        WriteError(context, "WRONGTYPE Operation against a key holding the wrong kind of value");
    }
}
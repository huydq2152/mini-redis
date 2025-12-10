using MyRedis.Abstractions;

namespace MyRedis.Services;

/// <summary>
/// Registry for command handlers
/// </summary>
public class CommandRegistry : ICommandRegistry
{
    private readonly Dictionary<string, ICommandHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ICommandHandler handler)
    {
        if (handler == null) 
            throw new ArgumentNullException(nameof(handler));

        _handlers[handler.CommandName] = handler;
    }

    public ICommandHandler? GetHandler(string commandName)
    {
        return _handlers.TryGetValue(commandName, out var handler) ? handler : null;
    }

    public IEnumerable<ICommandHandler> GetAllHandlers()
    {
        return _handlers.Values;
    }
}
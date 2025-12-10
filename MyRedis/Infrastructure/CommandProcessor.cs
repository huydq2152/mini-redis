using MyRedis.Abstractions;
using MyRedis.Core;

namespace MyRedis.Infrastructure;

/// <summary>
/// Handles Redis protocol parsing and command execution
/// Single Responsibility: Protocol parsing and command routing
/// </summary>
public class CommandProcessor
{
    private readonly ICommandRegistry _commandRegistry;
    private readonly IDataStore _dataStore;
    private readonly IExpirationService _expirationService;
    private readonly IResponseWriter _responseWriter;

    public CommandProcessor(
        ICommandRegistry commandRegistry,
        IDataStore dataStore,
        IExpirationService expirationService,
        IResponseWriter responseWriter)
    {
        _commandRegistry = commandRegistry ?? throw new ArgumentNullException(nameof(commandRegistry));
        _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        _expirationService = expirationService ?? throw new ArgumentNullException(nameof(expirationService));
        _responseWriter = responseWriter ?? throw new ArgumentNullException(nameof(responseWriter));
    }

    /// <summary>
    /// Processes incoming data and executes commands
    /// </summary>
    /// <param name="connection">The connection that received data</param>
    /// <returns>Number of commands processed</returns>
    public async Task<int> ProcessConnectionDataAsync(Connection connection)
    {
        int commandsProcessed = 0;

        // Loop to handle pipelining: client may send multiple commands at once
        while (true)
        {
            if (ProtocolParser.TryParse(connection.ReadBuffer, connection.BytesRead, 
                out var cmd, out int consumed))
            {
                // Successfully parsed one command
                Console.WriteLine($"[Command] {string.Join(" ", cmd)}");

                await ExecuteCommandAsync(connection, cmd);
                
                // Send response immediately (flush)
                connection.Flush();
                
                // Remove processed command from buffer
                connection.ShiftBuffer(consumed);
                commandsProcessed++;
            }
            else
            {
                // Not enough data for a complete command
                // Exit loop, wait for next Select() iteration to read more data
                break;
            }
        }

        return commandsProcessed;
    }

    private async Task ExecuteCommandAsync(Connection connection, List<string> cmd)
    {
        if (cmd.Count == 0) return;

        string commandName = cmd[0].ToUpper();
        var handler = _commandRegistry.GetHandler(commandName);

        if (handler != null)
        {
            var context = new Services.CommandContext(connection, _dataStore, _expirationService, _responseWriter);
            var args = cmd.Skip(1).ToList();
            
            await handler.HandleAsync(context, args);
        }
        else
        {
            _responseWriter.WriteError(connection.WriteBuffer, 1, "Unknown cmd");
        }
    }
}
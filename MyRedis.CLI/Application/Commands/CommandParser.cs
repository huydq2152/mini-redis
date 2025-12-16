using MyRedis.CLI.Domain.Commands;

namespace MyRedis.CLI.Application.Commands;

/// <summary>
/// Application service for parsing command line input into Redis commands.
/// Implements domain interface with validation and error handling.
/// </summary>
public sealed class CommandParser : ICommandParser
{
    public RedisCommand ParseCommand(string commandLine)
    {
        return RedisCommand.Parse(commandLine);
    }

    public bool TryParseCommand(string commandLine, out RedisCommand? command)
    {
        command = null;
        
        try
        {
            command = ParseCommand(commandLine);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
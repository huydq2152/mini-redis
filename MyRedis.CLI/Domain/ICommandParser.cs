namespace MyRedis.CLI.Domain;

/// <summary>
/// Interface for parsing command line input into Redis commands.
/// Supports different parsing strategies and validation rules.
/// </summary>
public interface ICommandParser
{
    /// <summary>
    /// Parses a command line string into a RedisCommand.
    /// </summary>
    /// <param name="commandLine">The command line to parse</param>
    /// <returns>A parsed RedisCommand</returns>
    /// <exception cref="ArgumentException">Thrown when the command line is invalid</exception>
    RedisCommand ParseCommand(string commandLine);
    
    /// <summary>
    /// Tries to parse a command line string into a RedisCommand.
    /// </summary>
    /// <param name="commandLine">The command line to parse</param>
    /// <param name="command">The parsed command, if successful</param>
    /// <returns>True if parsing was successful, false otherwise</returns>
    bool TryParseCommand(string commandLine, out RedisCommand? command);
}
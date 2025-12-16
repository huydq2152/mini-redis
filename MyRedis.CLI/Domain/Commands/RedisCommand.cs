using System.Text;

namespace MyRedis.CLI.Domain.Commands;

/// <summary>
/// Represents a Redis command with its arguments.
/// Immutable value object that encapsulates command parsing and validation.
/// </summary>
public sealed class RedisCommand
{
    /// <summary>
    /// Gets the command name (e.g., "GET", "SET", "ZADD").
    /// </summary>
    public string Name { get; }
    
    /// <summary>
    /// Gets the command arguments.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; }
    
    /// <summary>
    /// Gets the original command line that was parsed to create this command.
    /// </summary>
    public string OriginalText { get; }
    
    /// <summary>
    /// Initializes a new instance of the RedisCommand class.
    /// </summary>
    /// <param name="name">The command name</param>
    /// <param name="arguments">The command arguments</param>
    /// <param name="originalText">The original command text</param>
    public RedisCommand(string name, IReadOnlyList<string> arguments, string originalText)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        OriginalText = originalText ?? throw new ArgumentNullException(nameof(originalText));
    }
    
    /// <summary>
    /// Gets all command parts (name + arguments) as a single collection.
    /// </summary>
    public IReadOnlyList<string> AllParts
    {
        get
        {
            var result = new List<string> { Name };
            result.AddRange(Arguments);
            return result;
        }
    }
    
    /// <summary>
    /// Parses a command line string into a RedisCommand.
    /// Supports quoted strings and handles escape sequences.
    /// </summary>
    /// <param name="commandLine">The command line to parse</param>
    /// <returns>A parsed RedisCommand</returns>
    /// <exception cref="ArgumentException">Thrown when the command line is empty or invalid</exception>
    public static RedisCommand Parse(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            throw new ArgumentException("Command line cannot be empty", nameof(commandLine));
            
        var parts = ParseCommandLine(commandLine.Trim());
        if (parts.Length == 0)
            throw new ArgumentException("Command line must contain at least a command name", nameof(commandLine));
            
        var name = parts[0].ToUpperInvariant();
        var arguments = parts.Skip(1).ToList();
        
        return new RedisCommand(name, arguments, commandLine);
    }
    
    /// <summary>
    /// Parses a command line string into an array of arguments.
    /// Supports quoted strings with spaces and escape sequences.
    /// </summary>
    private static string[] ParseCommandLine(string commandLine)
    {
        var args = new List<string>();
        var currentArg = new StringBuilder();
        bool inQuotes = false;
        char quoteChar = '\0';

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];

            if (!inQuotes && (c == '"' || c == '\''))
            {
                // Start of quoted string
                inQuotes = true;
                quoteChar = c;
            }
            else if (inQuotes && c == quoteChar)
            {
                // End of quoted string
                inQuotes = false;
                quoteChar = '\0';
            }
            else if (!inQuotes && char.IsWhiteSpace(c))
            {
                // Whitespace outside quotes - end of argument
                if (currentArg.Length > 0)
                {
                    args.Add(currentArg.ToString());
                    currentArg.Clear();
                }
            }
            else
            {
                // Regular character - add to current argument
                currentArg.Append(c);
            }
        }

        // Add the last argument if any
        if (currentArg.Length > 0)
        {
            args.Add(currentArg.ToString());
        }

        return args.ToArray();
    }
    
    public override string ToString() => OriginalText;
}
using System.Text;

namespace MyRedis.CLI.Domain;

/// <summary>
/// Represents a response from a Redis server.
/// Immutable value object that encapsulates different response types.
/// </summary>
public abstract class RedisResponse
{
    /// <summary>
    /// Gets the response type.
    /// </summary>
    public abstract RedisResponseType Type { get; }
    
    /// <summary>
    /// Formats the response for display in the CLI.
    /// </summary>
    /// <param name="indent">The indentation level for nested structures</param>
    /// <returns>A formatted string representation</returns>
    public abstract string Format(int indent = 0);
}

/// <summary>
/// Represents the type of Redis response.
/// </summary>
public enum RedisResponseType
{
    Nil = 0,
    Error = 1,
    String = 2,
    Integer = 3,
    Array = 4
}

/// <summary>
/// Represents a nil (null) response.
/// </summary>
public sealed class NilResponse : RedisResponse
{
    public static readonly NilResponse Instance = new();
    
    private NilResponse() { }
    
    public override RedisResponseType Type => RedisResponseType.Nil;
    
    public override string Format(int indent = 0) => "(nil)";
}

/// <summary>
/// Represents an error response.
/// </summary>
public sealed class ErrorResponse : RedisResponse
{
    public int Code { get; }
    public string Message { get; }
    
    public ErrorResponse(int code, string message)
    {
        Code = code;
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }
    
    public override RedisResponseType Type => RedisResponseType.Error;
    
    public override string Format(int indent = 0) => $"(error) {Message}";
}

/// <summary>
/// Represents a string response.
/// </summary>
public sealed class StringResponse : RedisResponse
{
    public string Value { get; }
    
    public StringResponse(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }
    
    public override RedisResponseType Type => RedisResponseType.String;
    
    public override string Format(int indent = 0) => $"\"{Value}\"";
}

/// <summary>
/// Represents an integer response.
/// </summary>
public sealed class IntegerResponse : RedisResponse
{
    public long Value { get; }
    
    public IntegerResponse(long value)
    {
        Value = value;
    }
    
    public override RedisResponseType Type => RedisResponseType.Integer;
    
    public override string Format(int indent = 0) => $"(integer) {Value}";
}

/// <summary>
/// Represents an array response.
/// </summary>
public sealed class ArrayResponse : RedisResponse
{
    public IReadOnlyList<RedisResponse> Elements { get; }
    
    public ArrayResponse(IReadOnlyList<RedisResponse> elements)
    {
        Elements = elements ?? throw new ArgumentNullException(nameof(elements));
    }
    
    public override RedisResponseType Type => RedisResponseType.Array;
    
    public override string Format(int indent = 0)
    {
        if (Elements.Count == 0)
            return "(empty array)";
            
        var result = new StringBuilder();
        string indentStr = new string(' ', indent * 2);

        for (int i = 0; i < Elements.Count; i++)
        {
            if (i > 0) result.AppendLine();
            result.Append($"{indentStr}{i + 1}) ");

            var element = Elements[i];
            if (element.Type == RedisResponseType.Array)
            {
                result.AppendLine();
                result.Append(indentStr + "  " + element.Format(indent + 1));
            }
            else
            {
                result.Append(element.Format(indent + 1));
            }
        }

        return result.ToString();
    }
}
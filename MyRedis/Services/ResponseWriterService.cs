using MyRedis.Abstractions;
using MyRedis.Core;

namespace MyRedis.Services;

/// <summary>
/// Service adapter that provides Redis protocol response formatting by wrapping
/// the existing static ResponseWriter implementation.
///
/// Design Pattern: Adapter Pattern
/// This class adapts the static ResponseWriter utility class to the IResponseWriter interface,
/// enabling dependency injection and providing a testable abstraction for response formatting.
/// This allows command handlers to be unit tested with mock response writers.
///
/// Redis Binary Protocol Format:
/// All responses follow a binary protocol with type-length-value encoding:
/// 
/// Type 0 (Nil):     [0x00]
/// Type 1 (Error):   [0x01][4-byte code][4-byte msg len][UTF-8 message]
/// Type 2 (String):  [0x02][4-byte length][UTF-8 string data]
/// Type 3 (Integer): [0x03][8-byte int64 little-endian]
/// Type 4 (Array):   [0x04][4-byte count][element1][element2]...
///
/// Usage Pattern:
/// Command handlers receive this service through ICommandContext and use it to:
/// 1. Format responses according to the binary protocol specification
/// 2. Write responses directly to the client's connection buffer
/// 3. Ensure consistent response formatting across all commands
/// 4. Handle different response types (success, error, data) uniformly
///
/// Buffer Management:
/// - All methods append to the provided List<byte> buffer
/// - Buffer is managed by the Connection object
/// - Buffer is flushed to the network after command completion
/// - No buffering logic here, just protocol serialization
///
/// Thread Safety:
/// - This adapter is stateless and thread-safe
/// - The underlying static ResponseWriter is also thread-safe
/// - Buffer modification is safe as each connection has its own buffer
/// </summary>
public class ResponseWriterService : IResponseWriter
{
    /// <summary>
    /// Writes a string response to the client buffer using the binary protocol format.
    /// Handles UTF-8 encoding and proper length prefixing for variable-length strings.
    /// </summary>
    /// <param name="buffer">The connection's write buffer to append the response to</param>
    /// <param name="value">The string value to send to the client</param>
    /// <remarks>
    /// Protocol Format: [Type 2][4-byte length][UTF-8 bytes]
    /// 
    /// Used by commands such as:
    /// - GET: Return stored string values
    /// - ECHO: Return the echoed argument
    /// - PING: Return "PONG" response
    /// - Status responses: "OK" confirmations
    /// 
    /// The string is encoded as UTF-8 to support international characters.
    /// Length field contains byte count, not character count (important for multibyte chars).
    /// Delegates to the static ResponseWriter for actual protocol implementation.
    /// </remarks>
    public void WriteString(List<byte> buffer, string value)
    {
        ResponseWriter.WriteString(buffer, value);
    }

    /// <summary>
    /// Writes an integer response to the client buffer using 64-bit little-endian format.
    /// Provides consistent numeric response formatting for all integer-returning commands.
    /// </summary>
    /// <param name="buffer">The connection's write buffer to append the response to</param>
    /// <param name="value">The integer value to send to the client</param>
    /// <remarks>
    /// Protocol Format: [Type 3][8-byte int64 little-endian]
    /// 
    /// Used by commands such as:
    /// - DEL: Number of keys successfully deleted
    /// - TTL: Remaining seconds until expiration (-1 for no expiration, -2 for non-existent key)
    /// - ZADD: Number of new elements added to sorted set
    /// - DBSIZE: Total number of keys in database
    /// 
    /// Always uses 64-bit signed integer for consistency across all numeric responses.
    /// Little-endian format matches x86/x64 architecture for optimal performance.
    /// Total response size is always 9 bytes (1 type + 8 data).
    /// </remarks>
    public void WriteInt(List<byte> buffer, long value)
    {
        ResponseWriter.WriteInt(buffer, value);
    }

    /// <summary>
    /// Writes a nil (null) response to the client buffer for non-existent or null values.
    /// This is the most compact response type, consisting of only a single type byte.
    /// </summary>
    /// <param name="buffer">The connection's write buffer to append the response to</param>
    /// <remarks>
    /// Protocol Format: [Type 0] (single byte)
    /// 
    /// Used when:
    /// - GET command on non-existent keys
    /// - GET command on expired keys (after lazy expiration)
    /// - Commands that return no meaningful value
    /// - SET command success indication (alternative to "OK")
    /// 
    /// This is equivalent to Redis RESP protocol's "$-1\r\n" (null bulk string).
    /// Smallest possible response at just 1 byte total.
    /// Indicates "no data" or "operation completed without return value".
    /// </remarks>
    public void WriteNil(List<byte> buffer)
    {
        ResponseWriter.WriteNil(buffer);
    }

    /// <summary>
    /// Writes an error response to the client buffer with error code and descriptive message.
    /// Provides standardized error reporting for command validation failures and execution errors.
    /// </summary>
    /// <param name="buffer">The connection's write buffer to append the response to</param>
    /// <param name="code">Error code (currently standardized to 1, but extensible for future error types)</param>
    /// <param name="message">Human-readable error description for client debugging</param>
    /// <remarks>
    /// Protocol Format: [Type 1][4-byte code][4-byte msg len][UTF-8 message]
    /// 
    /// Common error scenarios:
    /// - "ERR wrong number of arguments" (invalid argument count)
    /// - "WRONGTYPE Operation against a key holding the wrong kind of value" (type mismatch)
    /// - "ERR value is not an integer" (parsing failures)
    /// - "ERR unknown command" (unrecognized command names)
    /// 
    /// Error Code Usage:
    /// - Currently all errors use code 1 for simplicity
    /// - Future enhancement could use different codes for error categories
    /// - Code could enable client-side error handling automation
    /// 
    /// Message encoding uses UTF-8 to support localized error messages.
    /// Compatible with Redis error response conventions for client compatibility.
    /// </remarks>
    public void WriteError(List<byte> buffer, int code, string message)
    {
        ResponseWriter.WriteError(buffer, code, message);
    }

    /// <summary>
    /// Writes an array header to begin a multi-element response sequence.
    /// Must be followed by exactly the specified number of element responses.
    /// </summary>
    /// <param name="buffer">The connection's write buffer to append the response to</param>
    /// <param name="count">Number of elements that will follow this header</param>
    /// <remarks>
    /// Protocol Format: [Type 4][4-byte count]
    /// 
    /// Usage Pattern:
    /// 1. Call WriteArrayHeader(buffer, N) to start an array of N elements
    /// 2. Call N response methods (WriteString, WriteInt, WriteNil, etc.)
    /// 3. Each element is a complete response that can be of any type
    /// 4. Arrays can be nested (elements can themselves be arrays)
    /// 
    /// Used by commands such as:
    /// - KEYS: Array of key name strings
    /// - ZRANGE: Array of sorted set member strings
    /// - MGET: Array of values (strings/nils) for multiple keys
    /// - Future: Any command returning multiple values
    /// 
    /// Example for ZRANGE returning ["Alice", "Bob", "Charlie"]:
    /// <code>
    /// WriteArrayHeader(buffer, 3);
    /// WriteString(buffer, "Alice");
    /// WriteString(buffer, "Bob");
    /// WriteString(buffer, "Charlie");
    /// </code>
    /// 
    /// Critical: Must write exactly 'count' elements after calling this method.
    /// Mismatched counts will result in protocol violations and client errors.
    /// </remarks>
    public void WriteArrayHeader(List<byte> buffer, int count)
    {
        ResponseWriter.WriteArrayHeader(buffer, count);
    }
}
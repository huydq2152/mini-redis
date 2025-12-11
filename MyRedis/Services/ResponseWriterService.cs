using System.Buffers;
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
/// Performance Optimization (Production-Grade):
/// Updated to use IBufferWriter<byte> instead of List<byte> for zero-allocation writes.
/// This eliminates GC pressure in high-throughput scenarios (C10K problem).
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
/// 2. Write responses directly to the client's connection buffer (via connection.Writer)
/// 3. Ensure consistent response formatting across all commands
/// 4. Handle different response types (success, error, data) uniformly
///
/// Buffer Management:
/// - All methods write to the provided IBufferWriter<byte> (from connection.Writer)
/// - Buffer is managed by the Connection object (ArrayBufferWriter internally)
/// - Buffer is flushed to the network after command completion
/// - No buffering logic here, just protocol serialization
/// - Zero-allocation: No intermediate buffers or arrays created
///
/// Thread Safety:
/// - This adapter is stateless and thread-safe
/// - The underlying static ResponseWriter is also thread-safe
/// - Buffer modification is safe as each connection has its own writer
/// </summary>
public class ResponseWriterService : IResponseWriter
{
    /// <summary>
    /// Writes a string response to the client buffer using the binary protocol format.
    /// Handles UTF-8 encoding and proper length prefixing for variable-length strings.
    /// </summary>
    /// <param name="writer">The buffer writer (from connection.Writer)</param>
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
    ///
    /// Performance: Zero-allocation via IBufferWriter<byte>.
    /// </remarks>
    public void WriteString(IBufferWriter<byte> writer, string value)
    {
        ResponseWriter.WriteString(writer, value);
    }

    /// <summary>
    /// Writes an integer response to the client buffer using 64-bit little-endian format.
    /// Provides consistent numeric response formatting for all integer-returning commands.
    /// </summary>
    /// <param name="writer">The buffer writer (from connection.Writer)</param>
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
    ///
    /// Performance: Zero-allocation via IBufferWriter<byte>.
    /// </remarks>
    public void WriteInt(IBufferWriter<byte> writer, long value)
    {
        ResponseWriter.WriteInt(writer, value);
    }

    /// <summary>
    /// Writes a nil (null) response to the client buffer for non-existent or null values.
    /// This is the most compact response type, consisting of only a single type byte.
    /// </summary>
    /// <param name="writer">The buffer writer (from connection.Writer)</param>
    /// <remarks>
    /// Protocol Format: [Type 0] (single byte)
    ///
    /// Used when:
    /// - GET command on a non-existent key
    /// - GET command on an expired key (TTL reached zero)
    /// - Any operation that returns "no value"
    ///
    /// This is the most efficient possible response - just 1 byte.
    /// Equivalent to Redis RESP protocol's "$-1\r\n" (bulk string null).
    /// Commonly used in cache-miss scenarios.
    ///
    /// Performance: Zero-allocation via IBufferWriter<byte>.
    /// </remarks>
    public void WriteNil(IBufferWriter<byte> writer)
    {
        ResponseWriter.WriteNil(writer);
    }

    /// <summary>
    /// Writes an error response to the client buffer with an error code and message.
    /// Provides structured error reporting for command failures and protocol violations.
    /// </summary>
    /// <param name="writer">The buffer writer (from connection.Writer)</param>
    /// <param name="code">Error code (currently always 1, but extensible for different error types)</param>
    /// <param name="message">Human-readable error message describing the failure</param>
    /// <remarks>
    /// Protocol Format: [Type 1][4-byte code][4-byte msg len][UTF-8 message]
    ///
    /// Used for various error conditions:
    /// - ERR wrong number of arguments: Command invoked with incorrect argument count
    /// - WRONGTYPE Operation against a key holding the wrong kind of value: Type mismatch
    /// - Unknown cmd: Command name not recognized
    ///
    /// The error code is currently not used extensively (always set to 1),
    /// but provides extensibility for categorizing errors in the future.
    ///
    /// Common error messages follow Redis conventions:
    /// - "ERR wrong number of arguments for 'commandname' command"
    /// - "WRONGTYPE Operation against a key holding the wrong kind of value"
    /// - "ERR value is not an integer or out of range"
    ///
    /// Performance: Zero-allocation via IBufferWriter<byte>.
    /// </remarks>
    public void WriteError(IBufferWriter<byte> writer, int code, string message)
    {
        ResponseWriter.WriteError(writer, code, message);
    }

    /// <summary>
    /// Writes an array header to the client buffer to begin a multi-element response.
    /// Must be followed by exactly the specified number of element writes.
    /// </summary>
    /// <param name="writer">The buffer writer (from connection.Writer)</param>
    /// <param name="count">Number of elements that will follow this header</param>
    /// <remarks>
    /// Protocol Format: [Type 4][4-byte count]
    ///
    /// After calling this method, the caller MUST write exactly 'count' elements
    /// using other Write methods (WriteString, WriteInt, WriteNil, or even WriteArrayHeader for nesting).
    ///
    /// Used by commands that return multiple values:
    /// - KEYS: Returns array of key name strings
    /// - ZRANGE: Returns array of sorted set member strings
    /// - MGET: Returns array of values (strings or nils)
    ///
    /// Example usage for ZRANGE returning ["Alice", "Bob", "Charlie"]:
    /// <code>
    /// writer.WriteArrayHeader(buffer, 3);
    /// writer.WriteString(buffer, "Alice");
    /// writer.WriteString(buffer, "Bob");
    /// writer.WriteString(buffer, "Charlie");
    /// </code>
    ///
    /// The protocol allows nested arrays (an element can itself be an array),
    /// enabling complex data structure responses.
    ///
    /// Performance: Zero-allocation via IBufferWriter<byte>.
    /// </remarks>
    public void WriteArrayHeader(IBufferWriter<byte> writer, int count)
    {
        ResponseWriter.WriteArrayHeader(writer, count);
    }
}

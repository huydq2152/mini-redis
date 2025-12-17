using System.Buffers;
using MyRedis.Abstractions.Network;
using MyRedis.Core.Network;

namespace MyRedis.Services.Network;

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
/// All responses follow a binary protocol with type-length-value encoding (start with a 1-byte type code):
///
/// Type 0 (Nil): [0x00]
///   - Represents null/non-existent values
///   - Used when GET fails to find a key
///   - Total size: 1 byte
///
/// Type 1 (Error): [0x01][4-byte code][4-byte msg len][UTF-8 message]
///   - Represents command execution errors
///   - Examples: wrong arguments, wrong type, unknown command
///   - Includes error code and human-readable message
///
/// Type 2 (String): [0x02][4-byte length][UTF-8 string data]
///   - Variable-length string responses
///   - Used for GET, ECHO, simple OK replies
///   - Length is in bytes (not characters)
///
/// Type 3 (Integer): [0x03][8-byte int64 little-endian]
///   - 64-bit signed integer responses
///   - Used for DEL (count), TTL (seconds), counters
///   - Always 9 bytes total (1 + 8)
///
/// Type 4 (Array): [0x04][4-byte count][element1][element2]...
///   - Multi-value responses
///   - Each element is a full response (can be nested)
///   - Used for KEYS, ZRANGE, multi-key operations
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
    /// Always uses 64-bit signed integer for consistency across all numeric responses.
    /// Little-endian format matches x86/x64 architecture for optimal performance, which helps "memcpy" operations
    /// avoid wasting CPU cycles to reverse byte order when forwarding data from server to client.
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
    /// The error code is currently not used extensively (always set to 1),
    /// but provides extensibility for categorizing errors in the future.
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

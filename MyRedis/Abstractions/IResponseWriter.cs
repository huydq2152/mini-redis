namespace MyRedis.Abstractions;

/// <summary>
/// Abstraction for writing responses to clients using the binary protocol.
///
/// Response Protocol Format:
/// All responses start with a 1-byte type code, followed by type-specific data.
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
/// Design Philosophy:
/// - Write methods append to the connection's write buffer
/// - Buffer is flushed after command completion
/// - No buffering logic here, just serialization
/// - All integers use little-endian (x86/x64 native)
/// </summary>
public interface IResponseWriter
{
    /// <summary>
    /// Writes a string response to the buffer.
    ///
    /// Format: [Type 2][4-byte length][UTF-8 bytes]
    ///
    /// Used by commands like:
    /// - GET (return stored value)
    /// - ECHO (return the argument)
    /// - PING (return "PONG")
    /// - SET (return "OK" confirmation)
    ///
    /// The string is encoded as UTF-8 to support international characters.
    /// Length is the byte count, not character count (important for multibyte chars).
    /// </summary>
    /// <param name="buffer">The connection's write buffer to append to</param>
    /// <param name="value">The string value to send to the client</param>
    void WriteString(List<byte> buffer, string value);

    /// <summary>
    /// Writes an integer response to the buffer.
    ///
    /// Format: [Type 3][8-byte int64 little-endian]
    ///
    /// Used by commands like:
    /// - DEL (number of keys deleted)
    /// - TTL (seconds remaining until expiration, or -1/-2 for special cases)
    /// - ZADD (number of elements added)
    /// - DBSIZE (total key count)
    ///
    /// Always uses 64-bit signed integer for consistency.
    /// Little-endian matches x86/x64 architecture for efficiency.
    /// </summary>
    /// <param name="buffer">The connection's write buffer to append to</param>
    /// <param name="value">The integer value to send to the client</param>
    void WriteInt(List<byte> buffer, long value);

    /// <summary>
    /// Writes a nil (null) response to the buffer.
    ///
    /// Format: [Type 0]
    ///
    /// Used when:
    /// - GET on a non-existent key
    /// - GET on an expired key
    /// - Any operation that returns "no value"
    ///
    /// This is the smallest possible response (1 byte).
    /// Equivalent to Redis's "$-1\r\n" (bulk string null) in RESP protocol.
    /// </summary>
    /// <param name="buffer">The connection's write buffer to append to</param>
    void WriteNil(List<byte> buffer);

    /// <summary>
    /// Writes an error response to the buffer.
    ///
    /// Format: [Type 1][4-byte code][4-byte msg len][UTF-8 message]
    ///
    /// Used for various error conditions:
    /// - ERR wrong number of arguments (code 1)
    /// - WRONGTYPE operation against wrong type (code 1)
    /// - Unknown command (code 1)
    ///
    /// The error code is currently not used extensively (always 1),
    /// but could be expanded for different error types.
    ///
    /// Examples:
    /// - GET with no args: "ERR wrong number of arguments"
    /// - ZADD on string key: "WRONGTYPE Operation against a key holding the wrong kind of value"
    /// </summary>
    /// <param name="buffer">The connection's write buffer to append to</param>
    /// <param name="code">Error code (currently always 1, but extensible)</param>
    /// <param name="message">Human-readable error message</param>
    void WriteError(List<byte> buffer, int code, string message);

    /// <summary>
    /// Writes an array header to the buffer (first part of an array response).
    ///
    /// Format: [Type 4][4-byte count]
    ///
    /// After calling this, you must write exactly 'count' elements using other Write methods.
    /// Each element is a complete response (String, Int, Nil, or even another Array).
    ///
    /// Used by commands that return multiple values:
    /// - KEYS (list of key names) - array of strings
    /// - ZRANGE (list of members) - array of strings
    /// - MGET (multiple values) - array of strings/nils
    ///
    /// Example for ZRANGE returning ["Alice", "Bob", "Charlie"]:
    /// 1. WriteArrayHeader(buffer, 3)
    /// 2. WriteString(buffer, "Alice")
    /// 3. WriteString(buffer, "Bob")
    /// 4. WriteString(buffer, "Charlie")
    ///
    /// The client will receive: [Type 4][3]["Alice"]["Bob"]["Charlie"]
    /// </summary>
    /// <param name="buffer">The connection's write buffer to append to</param>
    /// <param name="count">Number of elements that will follow</param>
    void WriteArrayHeader(List<byte> buffer, int count);
}
using System.Text;
using System.Buffers.Binary;

namespace MyRedis.Core
{
    /// <summary>
    /// Response type codes used in the binary protocol.
    ///
    /// Each response from the server starts with one of these type bytes,
    /// followed by type-specific data.
    ///
    /// Type byte format matches the protocol specification:
    /// - Type 0 (Nil): No additional data
    /// - Type 1 (Err): Error code + message
    /// - Type 2 (Str): Length + UTF-8 string data
    /// - Type 3 (Int): 8-byte int64 value
    /// - Type 4 (Arr): Count + elements
    /// </summary>
    public enum ResponseType : byte
    {
        /// <summary>Null/non-existent value (1 byte total)</summary>
        Nil = 0,

        /// <summary>Error response with code and message</summary>
        Err = 1,

        /// <summary>Variable-length UTF-8 string</summary>
        Str = 2,

        /// <summary>64-bit signed integer</summary>
        Int = 3,

        /// <summary>Array of values (each element is a full response)</summary>
        Arr = 4
    }

    /// <summary>
    /// Static utility class for writing responses in the binary protocol format.
    ///
    /// This is the concrete implementation of the IResponseWriter abstraction.
    /// All methods are static because response writing has no state.
    ///
    /// Protocol Design Principles:
    /// - Little-endian for all integers (matches x86/x64 architecture)
    /// - UTF-8 encoding for all strings (Unicode support)
    /// - Type-length-value format for variable-length data
    /// - Simple and efficient parsing on the client side
    ///
    /// Performance Considerations:
    /// - Uses stackalloc for small temporary buffers (no heap allocation)
    /// - Appends directly to the connection's write buffer (no intermediate copying)
    /// - Binary format is more compact than text-based protocols
    ///
    /// Related: See IResponseWriter interface for documentation of each response type.
    /// </summary>
    public static class ResponseWriter
    {
        /// <summary>
        /// Writes a 32-bit integer in little-endian format.
        ///
        /// Little-endian means the least significant byte comes first.
        /// Example: 300 (0x0000012C) becomes [2C 01 00 00] in memory.
        ///
        /// Used internally for:
        /// - String lengths
        /// - Array counts
        /// - Error codes
        ///
        /// Uses stackalloc for efficiency (4 bytes allocated on stack, not heap).
        /// </summary>
        private static void WriteInt32(List<byte> buffer, int value)
        {
            // Allocate 4 bytes on the stack for the integer
            Span<byte> span = stackalloc byte[4];

            // Write the integer in little-endian format
            BinaryPrimitives.WriteInt32LittleEndian(span, value);

            // Append the 4 bytes to the buffer
            buffer.AddRange(span.ToArray());
        }

        /// <summary>
        /// Writes a 64-bit integer in little-endian format.
        ///
        /// Used for Type 3 (Int) responses.
        /// 64-bit integers provide large enough range for most use cases:
        /// - Counters that may grow very large
        /// - TTL values (milliseconds can be large numbers)
        /// - Database sizes and statistics
        ///
        /// Uses stackalloc for efficiency (8 bytes on stack).
        /// </summary>
        private static void WriteInt64(List<byte> buffer, long value)
        {
            // Allocate 8 bytes on the stack for the long integer
            Span<byte> span = stackalloc byte[8];

            // Write the integer in little-endian format
            BinaryPrimitives.WriteInt64LittleEndian(span, value);

            // Append the 8 bytes to the buffer
            buffer.AddRange(span.ToArray());
        }

        /// <summary>
        /// Writes a nil (null) response to the buffer.
        ///
        /// Format: [Type 0]
        /// Total size: 1 byte
        ///
        /// This is the most efficient response possible.
        /// Used when a key doesn't exist or has no value.
        /// </summary>
        public static void WriteNil(List<byte> buffer)
        {
            buffer.Add((byte)ResponseType.Nil);
        }

        /// <summary>
        /// Writes a string response to the buffer.
        ///
        /// Format: [Type 2][4-byte length][UTF-8 string bytes]
        ///
        /// The length is the byte count, not character count.
        /// This matters for multi-byte UTF-8 characters.
        ///
        /// Example for "Hello" (5 characters, 5 bytes):
        /// [0x02][05 00 00 00][48 65 6C 6C 6F]
        ///
        /// Example for "你好" (2 characters, 6 bytes in UTF-8):
        /// [0x02][06 00 00 00][E4 BD A0 E5 A5 BD]
        /// </summary>
        public static void WriteString(List<byte> buffer, string value)
        {
            // Write the type byte
            buffer.Add((byte)ResponseType.Str);

            // Convert string to UTF-8 bytes
            var bytes = Encoding.UTF8.GetBytes(value);

            // Write the length (number of bytes, not characters)
            WriteInt32(buffer, bytes.Length);

            // Write the actual string content
            buffer.AddRange(bytes);
        }

        /// <summary>
        /// Writes an integer response to the buffer.
        ///
        /// Format: [Type 3][8-byte int64 little-endian]
        /// Total size: 9 bytes (1 + 8)
        ///
        /// Used for numeric responses like:
        /// - DEL command: number of keys deleted
        /// - TTL command: seconds remaining (-1 = no expiration, -2 = key doesn't exist)
        /// - ZADD command: number of elements added
        ///
        /// Always uses 64-bit even for small numbers for protocol consistency.
        /// </summary>
        public static void WriteInt(List<byte> buffer, long value)
        {
            // Write the type byte
            buffer.Add((byte)ResponseType.Int);

            // Write the 8-byte integer value
            WriteInt64(buffer, value);
        }

        /// <summary>
        /// Writes an error response to the buffer.
        ///
        /// Format: [Type 1][4-byte code][4-byte message length][UTF-8 message]
        ///
        /// Error codes (currently not heavily used, always 1):
        /// - 1: General command error
        ///
        /// Common error messages:
        /// - "ERR wrong number of arguments"
        /// - "WRONGTYPE Operation against a key holding the wrong kind of value"
        /// - "Unknown cmd"
        ///
        /// The client should display the message to help debug the issue.
        /// </summary>
        public static void WriteError(List<byte> buffer, int code, string message)
        {
            // Write the type byte
            buffer.Add((byte)ResponseType.Err);

            // Write the error code (4 bytes)
            WriteInt32(buffer, code);

            // Convert error message to UTF-8 bytes
            var msgBytes = Encoding.UTF8.GetBytes(message);

            // Write the message length (4 bytes)
            WriteInt32(buffer, msgBytes.Length);

            // Write the error message content
            buffer.AddRange(msgBytes);
        }

        /// <summary>
        /// Writes an array header to the buffer.
        ///
        /// Format: [Type 4][4-byte count]
        ///
        /// This only writes the header. The caller must then write exactly 'count'
        /// elements using other Write methods (WriteString, WriteInt, etc.).
        ///
        /// Used for commands returning multiple values:
        /// - KEYS: array of key name strings
        /// - ZRANGE: array of member name strings
        ///
        /// Example for ["apple", "banana", "cherry"]:
        /// 1. WriteArrayHeader(buffer, 3)
        /// 2. WriteString(buffer, "apple")
        /// 3. WriteString(buffer, "banana")
        /// 4. WriteString(buffer, "cherry")
        ///
        /// Arrays can be nested (an element can itself be an array).
        /// </summary>
        public static void WriteArrayHeader(List<byte> buffer, int count)
        {
            // Write the type byte
            buffer.Add((byte)ResponseType.Arr);

            // Write the element count (4 bytes)
            WriteInt32(buffer, count);
        }
    }
}
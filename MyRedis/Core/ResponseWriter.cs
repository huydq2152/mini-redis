using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace MyRedis.Core;

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
/// Performance Optimizations (Production-Grade):
/// - Zero-Allocation: Writes directly to IBufferWriter<byte> (no intermediate buffers)
/// - Zero-Copy: Uses Span<byte> for direct memory access
/// - Stack-based: BinaryPrimitives writes directly to buffer spans (no ToArray())
///
/// Performance Impact (10K requests/sec):
/// - Old approach (List<byte> + ToArray()): 10K allocations/sec = GC pressure
/// - New approach (IBufferWriter): 0 allocations = no GC pauses
///
/// Related: See IResponseWriter interface for documentation of each response type.
/// </summary>
public static class ResponseWriter
{
    /// <summary>
    /// Writes a 32-bit integer in little-endian format directly to the buffer writer.
    ///
    /// Zero-Allocation Strategy:
    /// 1. GetSpan(4) reserves 4 bytes in the writer's internal buffer
    /// 2. BinaryPrimitives.WriteInt32LittleEndian writes directly to that span
    /// 3. Advance(4) commits the write (no copying, no allocation)
    ///
    /// Little-endian means the least significant byte comes first.
    /// Example: 300 (0x0000012C) becomes [2C 01 00 00] in memory.
    ///
    /// Used internally for:
    /// - String lengths
    /// - Array counts
    /// - Error codes
    /// </summary>
    private static void WriteInt32(IBufferWriter<byte> writer, int value)
    {
        // Get a span of at least 4 bytes from the writer's buffer
        Span<byte> span = writer.GetSpan(4);

        // Write the integer directly to the buffer (zero-copy)
        BinaryPrimitives.WriteInt32LittleEndian(span, value);

        // Commit the write (advance the write position by 4 bytes)
        writer.Advance(4);
    }

    /// <summary>
    /// Writes a 64-bit integer in little-endian format directly to the buffer writer.
    ///
    /// Zero-Allocation Strategy:
    /// Same as WriteInt32, but for 8 bytes instead of 4.
    ///
    /// Used for Type 3 (Int) responses.
    /// 64-bit integers provide large enough range for most use cases:
    /// - Counters that may grow very large
    /// - TTL values (milliseconds can be large numbers)
    /// - Database sizes and statistics
    /// </summary>
    private static void WriteInt64(IBufferWriter<byte> writer, long value)
    {
        // Get a span of at least 8 bytes from the writer's buffer
        Span<byte> span = writer.GetSpan(8);

        // Write the long integer directly to the buffer (zero-copy)
        BinaryPrimitives.WriteInt64LittleEndian(span, value);

        // Commit the write (advance the write position by 8 bytes)
        writer.Advance(8);
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
    public static void WriteNil(IBufferWriter<byte> writer)
    {
        // Get 1 byte from the writer's buffer
        Span<byte> span = writer.GetSpan(1);

        // Write the type byte directly
        span[0] = (byte)ResponseType.Nil;

        // Commit the write
        writer.Advance(1);
    }

    /// <summary>
    /// Writes a string response to the buffer.
    ///
    /// Format: [Type 2][4-byte length][UTF-8 string bytes]
    ///
    /// Zero-Allocation Strategy:
    /// 1. GetByteCount() calculates byte length without allocation
    /// 2. GetSpan() reserves exact space needed
    /// 3. GetBytes() encodes directly into the span (no intermediate array)
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
    public static void WriteString(IBufferWriter<byte> writer, string value)
    {
        // Step 1: Write type byte
        Span<byte> typeSpan = writer.GetSpan(1);
        typeSpan[0] = (byte)ResponseType.Str;
        writer.Advance(1);

        // Step 2: Calculate UTF-8 byte count without allocating
        int byteCount = Encoding.UTF8.GetByteCount(value);

        // Step 3: Write length
        WriteInt32(writer, byteCount);

        // Step 4: Write string content directly into buffer (zero-copy)
        Span<byte> strSpan = writer.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(value, strSpan);
        writer.Advance(byteCount);
    }

    /// <summary>
    /// Writes an integer response to the buffer.
    ///
    /// Format: [Type 3][8-byte int64 little-endian]
    /// Total size: 9 bytes (1 + 8)
    ///
    /// Zero-Allocation: Single GetSpan(9) for type + value, no intermediate allocations.
    ///
    /// Used for numeric responses like:
    /// - DEL command: number of keys deleted
    /// - TTL command: seconds remaining (-1 = no expiration, -2 = key doesn't exist)
    /// - ZADD command: number of elements added
    ///
    /// Always uses 64-bit even for small numbers for protocol consistency.
    /// </summary>
    public static void WriteInt(IBufferWriter<byte> writer, long value)
    {
        // Get 9 bytes: 1 for type + 8 for int64
        Span<byte> span = writer.GetSpan(9);

        // Write type byte
        span[0] = (byte)ResponseType.Int;

        // Write the 8-byte integer value directly
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(1), value);

        // Commit the write
        writer.Advance(9);
    }

    /// <summary>
    /// Writes an error response to the buffer.
    ///
    /// Format: [Type 1][4-byte code][4-byte message length][UTF-8 message]
    ///
    /// Zero-Allocation: Same strategy as WriteString - calculate byte count, then encode directly.
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
    public static void WriteError(IBufferWriter<byte> writer, int code, string message)
    {
        // Step 1: Write type byte
        Span<byte> typeSpan = writer.GetSpan(1);
        typeSpan[0] = (byte)ResponseType.Err;
        writer.Advance(1);

        // Step 2: Write error code
        WriteInt32(writer, code);

        // Step 3: Calculate message byte count without allocating
        int msgByteCount = Encoding.UTF8.GetByteCount(message);

        // Step 4: Write message length
        WriteInt32(writer, msgByteCount);

        // Step 5: Write message content directly into buffer (zero-copy)
        Span<byte> msgSpan = writer.GetSpan(msgByteCount);
        Encoding.UTF8.GetBytes(message, msgSpan);
        writer.Advance(msgByteCount);
    }

    /// <summary>
    /// Writes an array header to the buffer.
    ///
    /// Format: [Type 4][4-byte count]
    ///
    /// Zero-Allocation: Simple type byte + count write, no allocations.
    ///
    /// This only writes the header. The caller must then write exactly 'count'
    /// elements using other Write methods (WriteString, WriteInt, etc.).
    ///
    /// Used for commands returning multiple values:
    /// - KEYS: array of key name strings
    /// - ZRANGE: array of member name strings
    ///
    /// Example for ["apple", "banana", "cherry"]:
    /// 1. WriteArrayHeader(writer, 3)
    /// 2. WriteString(writer, "apple")
    /// 3. WriteString(writer, "banana")
    /// 4. WriteString(writer, "cherry")
    ///
    /// Arrays can be nested (an element can itself be an array).
    /// </summary>
    public static void WriteArrayHeader(IBufferWriter<byte> writer, int count)
    {
        // Write type byte
        Span<byte> typeSpan = writer.GetSpan(1);
        typeSpan[0] = (byte)ResponseType.Arr;
        writer.Advance(1);

        // Write the element count (4 bytes)
        WriteInt32(writer, count);
    }
}
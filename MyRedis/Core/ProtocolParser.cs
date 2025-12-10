using System.Buffers.Binary;
using System.Text;

namespace MyRedis.Core;

/// <summary>
/// Parses the binary protocol used for client-server communication.
///
/// Protocol Format (Request):
/// [4 bytes: argument count]
/// [4 bytes: length of arg1][arg1 UTF-8 bytes]
/// [4 bytes: length of arg2][arg2 UTF-8 bytes]
/// ...
///
/// Example for "SET key value":
/// [00 00 00 03]              // 3 arguments
/// [00 00 00 03]["SET"]       // "SET" (3 bytes)
/// [00 00 00 03]["key"]       // "key" (3 bytes)
/// [00 00 00 05]["value"]     // "value" (5 bytes)
///
/// Design Principles:
/// - Simple binary format for fast parsing
/// - Little-endian integers (matches x86/x64)
/// - Length-prefixed strings (no delimiters needed)
/// - Self-describing (count tells parser how many arguments to expect)
///
/// Streaming Protocol Challenges:
/// - Commands arrive incrementally over TCP
/// - A single recv() may contain: partial command, full command, or multiple commands
/// - Parser must handle incomplete data gracefully
/// - TryParse returns false if not enough data, true if complete command parsed
///
/// Security Considerations:
/// - Limit argument count to prevent memory exhaustion attacks
/// - Validate lengths before allocating
/// - Use Span<byte> to avoid unnecessary allocations
///
/// Thread Safety: Static methods, no shared state, thread-safe.
/// </summary>
public static class ProtocolParser
{
    /// <summary>
    /// Attempts to parse one complete command from the buffer.
    ///
    /// Returns:
    /// - true: Successfully parsed a complete command
    ///   - command: List of argument strings (command[0] is the command name)
    ///   - bytesConsumed: Number of bytes used from the buffer
    /// - false: Not enough data for a complete command yet
    ///   - command: null
    ///   - bytesConsumed: 0
    ///   - Caller should read more data from socket and try again
    ///
    /// Streaming Protocol Behavior:
    /// The parser is designed for incremental parsing:
    /// 1. Client sends data over TCP (may be fragmented)
    /// 2. Server accumulates data in a buffer
    /// 3. TryParse attempts to extract a command
    /// 4. If successful: remove consumed bytes, process command
    /// 5. If not: wait for more data
    ///
    /// Pipelining Support:
    /// Multiple commands may arrive in one TCP packet:
    /// - Parse first command
    /// - Remove consumed bytes from buffer (ShiftBuffer)
    /// - Try to parse again (loop until TryParse returns false)
    ///
    /// Example Usage:
    /// <code>
    /// while (TryParse(buffer, bytesRead, out var cmd, out int consumed)) {
    ///     ProcessCommand(cmd);
    ///     ShiftBuffer(consumed);
    /// }
    /// </code>
    ///
    /// Security:
    /// - Throws if argument count > 1024 (DoS protection)
    /// - Validates lengths before reading data
    /// - Uses Span to avoid bounds-checking overhead
    /// </summary>
    /// <param name="buffer">The receive buffer containing incoming data</param>
    /// <param name="dataLen">Number of valid bytes in the buffer</param>
    /// <param name="command">Output: Parsed command arguments (null if incomplete)</param>
    /// <param name="bytesConsumed">Output: Bytes used from buffer (0 if incomplete)</param>
    /// <returns>True if a complete command was parsed, false if more data needed</returns>
    /// <exception cref="Exception">Thrown if argument count exceeds safety limit</exception>
    public static bool TryParse(byte[] buffer, int dataLen, out List<string> command, out int bytesConsumed)
    {
        // Initialize output parameters for failure case
        command = null;
        bytesConsumed = 0;

        // Use Span for efficient, bounds-checked memory access
        // Span avoids array bounds checks on each access (JIT optimization)
        var span = buffer.AsSpan(0, dataLen);
        int offset = 0;

        // Step 1: Read the argument count header (4 bytes)
        if (span.Length < 4)
            return false; // Need at least 4 bytes for the count

        // Read argument count as 32-bit unsigned little-endian integer
        uint nStr = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;

        // Security: Limit argument count to prevent memory exhaustion
        // Redis commands typically have 1-10 arguments
        // 1024 is generous but prevents DoS attacks like:
        // - Sending huge argument count causes huge allocation
        // - Crashing server with OutOfMemoryException
        if (nStr > 1024)
            throw new Exception("Protocol Error: Too many arguments");

        // Pre-allocate list with exact capacity (avoids resizing)
        var result = new List<string>((int)nStr);

        // Step 2: Read each argument (length-prefixed string)
        for (int i = 0; i < nStr; i++)
        {
            // Check if we have 4 bytes for the string length
            if (span.Length - offset < 4)
                return false; // Incomplete - need more data

            // Read string length as 32-bit unsigned little-endian integer
            uint strLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
            offset += 4;

            // Check if we have all the string content
            if (span.Length - offset < strLen)
                return false; // Incomplete - need more data

            // Decode the UTF-8 string
            // Note: For performance, could keep as byte[] and decode lazily
            // Current approach prioritizes simplicity
            string str = Encoding.UTF8.GetString(span.Slice(offset, (int)strLen));
            result.Add(str);

            // Move offset past this string
            offset += (int)strLen;
        }

        // Success! We parsed a complete command
        command = result;
        bytesConsumed = offset; // Tell caller how many bytes we consumed
        return true;
    }
}
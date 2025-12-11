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
    /// Maps a byte span to an interned command name string for zero-allocation command parsing.
    ///
    /// Performance Optimization - String Interning:
    /// Redis command names are repeated millions of times (GET, SET, DEL, etc.).
    /// Instead of allocating a new string for each command, we return interned strings.
    ///
    /// Zero-Allocation Strategy:
    /// - Uses u8 literals (C# 11) for direct byte comparison (no string allocation)
    /// - SequenceEqual is optimized by JIT (SIMD vectorization)
    /// - Returns same string instance for same command (interning)
    ///
    /// Performance Impact (10K requests/sec):
    /// - Without interning: 10K string allocations/sec for command names = GC pressure
    /// - With interning: 0 allocations for known commands = no GC
    ///
    /// Case Handling:
    /// Redis commands are case-insensitive, but clients typically send uppercase.
    /// We check both uppercase and lowercase for compatibility.
    ///
    /// Fallback:
    /// Unknown commands still allocate (unavoidable), but this is rare
    /// (only custom commands or typos).
    /// </summary>
    /// <param name="span">The byte span containing the command name</param>
    /// <returns>Interned string for known commands, allocated string for unknown commands</returns>
    private static string MapCommand(ReadOnlySpan<byte> span)
    {
        // Hot path commands (90%+ of traffic in typical Redis workloads)
        // Checked first for optimal branch prediction
        if (span.SequenceEqual("GET"u8)) return "GET";
        if (span.SequenceEqual("SET"u8)) return "SET";
        if (span.SequenceEqual("DEL"u8)) return "DEL";

        // Common commands (sorted by typical usage frequency)
        if (span.SequenceEqual("PING"u8)) return "PING";
        if (span.SequenceEqual("ECHO"u8)) return "ECHO";
        if (span.SequenceEqual("KEYS"u8)) return "KEYS";
        if (span.SequenceEqual("TTL"u8)) return "TTL";
        if (span.SequenceEqual("EXPIRE"u8)) return "EXPIRE";

        // Sorted set commands
        if (span.SequenceEqual("ZADD"u8)) return "ZADD";
        if (span.SequenceEqual("ZRANGE"u8)) return "ZRANGE";

        // Case-insensitive support (lowercase variants)
        // Less common but supported for compatibility
        if (span.SequenceEqual("get"u8)) return "GET"; // Normalize to uppercase
        if (span.SequenceEqual("set"u8)) return "SET";
        if (span.SequenceEqual("del"u8)) return "DEL";
        if (span.SequenceEqual("ping"u8)) return "PING";
        if (span.SequenceEqual("echo"u8)) return "ECHO";
        if (span.SequenceEqual("keys"u8)) return "KEYS";
        if (span.SequenceEqual("ttl"u8)) return "TTL";
        if (span.SequenceEqual("expire"u8)) return "EXPIRE";
        if (span.SequenceEqual("zadd"u8)) return "ZADD";
        if (span.SequenceEqual("zrange"u8)) return "ZRANGE";

        // Fallback: Unknown command - must allocate
        // This is rare (custom commands, typos, or future commands)
        // Convert to uppercase for consistency with command registry
        return Encoding.UTF8.GetString(span).ToUpperInvariant();
    }

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
    public static bool TryParse(byte[] buffer, int dataLen, out List<string>? command, out int bytesConsumed)
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

            // Get the argument as a byte span
            ReadOnlySpan<byte> argSpan = span.Slice(offset, (int)strLen);

            // Performance Optimization: Use string interning for command name (first argument)
            // Command names are repeated millions of times (GET, SET, etc.)
            // Interning eliminates allocations for known commands
            string str;
            if (i == 0)
            {
                // First argument = command name
                // Use MapCommand to return interned string (zero allocation for known commands)
                str = MapCommand(argSpan);
            }
            else
            {
                // Subsequent arguments = keys, values, scores, etc.
                // These are unique per request, must allocate
                // No optimization possible here (unavoidable allocation)
                str = Encoding.UTF8.GetString(argSpan);
            }

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
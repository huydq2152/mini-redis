using System.Buffers;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MyRedis.Core;

/// <summary>
/// Represents a client connection to the Redis server.
///
/// This class encapsulates all per-connection state including:
/// - The underlying TCP socket
/// - Read buffer for incoming data (protocol parsing)
/// - Write buffer for outgoing responses
/// - Activity timestamp for idle detection
/// - Linked list node reference for idle tracking (intrusive pattern)
///
/// Buffer Management Strategy:
/// - Fixed-size read buffer (4KB) - sufficient for most Redis commands
/// - Dynamic write buffer (List<byte>) - grows as needed for responses
/// - Read buffer compaction (ShiftBuffer) to avoid copying
///
/// Lifecycle:
/// 1. Created by NetworkServer when a client connects
/// 2. Added to IdleManager for timeout tracking
/// 3. Read buffer accumulates incoming data
/// 4. ProtocolParser extracts complete commands
/// 5. Responses are written to write buffer
/// 6. Write buffer is flushed after each command
/// 7. Closed when client disconnects or idle timeout occurs
///
/// Thread Safety: Not thread-safe (single-threaded event loop).
/// </summary>
public class Connection
{
    /// <summary>
    /// The underlying TCP socket for this connection.
    ///
    /// Used by NetworkServer for:
    /// - Receiving data (Socket.Receive)
    /// - Sending data (Socket.Send)
    /// - Getting client address (Socket.RemoteEndPoint)
    /// - Closing the connection
    /// </summary>
    public Socket Socket { get; }

    /// <summary>
    /// Fixed-size buffer for incoming data from the client.
    ///
    /// Size: 4KB (4096 bytes)
    /// - Sufficient for most Redis commands
    /// - Small enough to avoid wasting memory per connection
    /// - Matches typical MTU sizes for efficient network I/O
    ///
    /// Redis protocol commands typically range from:
    /// - 20-100 bytes for simple commands (GET, SET)
    /// - 500-2000 bytes for complex commands (ZADD with multiple elements)
    /// - 4KB handles pipelined commands well
    ///
    /// When buffer fills up:
    /// - Commands are parsed and removed via ShiftBuffer()
    /// - This makes room for more data
    /// - If a single command exceeds 4KB, it would fail (protocol limit)
    /// </summary>
    public byte[] ReadBuffer { get; } = new byte[4096];

    /// <summary>
    /// Number of valid bytes currently in the ReadBuffer.
    ///
    /// Invariant: 0 <= BytesRead <= ReadBuffer.Length
    ///
    /// Usage:
    /// - NetworkServer increments this after Socket.Receive()
    /// - ProtocolParser reads from ReadBuffer[0..BytesRead-1]
    /// - ShiftBuffer() decrements this after removing parsed commands
    ///
    /// Example flow:
    /// 1. Socket.Receive() reads 50 bytes -> BytesRead = 50
    /// 2. Parser consumes 30 bytes (one command) -> ShiftBuffer(30)
    /// 3. ShiftBuffer moves remaining 20 bytes to start -> BytesRead = 20
    /// 4. Socket.Receive() reads 40 more bytes -> BytesRead = 60
    /// </summary>
    public int BytesRead { get; set; } = 0;

    /// <summary>
    /// Timestamp of the last activity on this connection (in milliseconds).
    ///
    /// Uses Environment.TickCount64 (monotonic clock):
    /// - Milliseconds since system boot
    /// - Immune to system clock changes
    /// - 64-bit prevents overflow
    ///
    /// Updated by:
    /// - IdleManager.Add() when connection is created
    /// - IdleManager.Touch() when data is received
    ///
    /// Used by:
    /// - IdleManager to detect idle connections
    /// - Idle timeout is typically 5 minutes (300,000ms)
    /// </summary>
    public long LastActive { get; set; } = Environment.TickCount64;

    /// <summary>
    /// Reference to this connection's node in the IdleManager's linked list.
    ///
    /// Intrusive Data Structure Pattern:
    /// - Instead of searching the list to find this connection (O(n))
    /// - We store a direct reference to its node
    /// - This enables O(1) removal and repositioning
    ///
    /// Managed by IdleManager:
    /// - Set by Add() when connection is added to tracking
    /// - Used by Touch() to move connection to end of list
    /// - Cleared by Remove() when connection is closed
    ///
    /// Null when:
    /// - Connection hasn't been added to IdleManager yet
    /// - Connection has been removed from tracking
    /// </summary>
    public LinkedListNode<Connection> Node { get; set; }

    /// <summary>
    /// Creates a new connection wrapper for the given socket.
    ///
    /// The socket should already be:
    /// - Connected to a client
    /// - Set to non-blocking mode
    /// - Configured with appropriate socket options
    ///
    /// All buffers and state are initialized with default values.
    /// </summary>
    public Connection(Socket socket)
    {
        Socket = socket;
    }
    
    /// <summary>
    /// Shifts (compacts) the read buffer by removing consumed bytes from the beginning.
    ///
    /// This is equivalent to the `memmove` operation described in Redis design literature.
    /// It's crucial for efficient buffer management in streaming protocols.
    ///
    /// Why We Need This:
    /// - Commands arrive as a stream of bytes
    /// - We parse one command at a time from the beginning of the buffer
    /// - After parsing, we need to remove the parsed bytes
    /// - The remaining bytes (partial next command) must stay for next parse attempt
    ///
    /// Algorithm:
    /// 1. Calculate how many bytes remain after consumption
    /// 2. Move remaining bytes to the start of the buffer (overlapping copy)
    /// 3. Update BytesRead to reflect the new amount of valid data
    ///
    /// Example:
    /// Buffer: [CMD1 DATA][CMD2 DATA][PARTIAL CMD3]
    /// After parsing CMD1 (consumed=15 bytes):
    /// - Move [CMD2 DATA][PARTIAL CMD3] to start
    /// - BytesRead -= 15
    ///
    /// Performance:
    /// - O(remaining bytes) copy operation
    /// - Array.Copy handles overlapping regions correctly
    /// - Typically remaining is small (0-100 bytes)
    /// - Much better than allocating a new buffer
    ///
    /// Alternative Approaches (Not Used):
    /// - Circular buffer: More complex, harder to integrate with Span/Memory
    /// - Multiple buffers: Higher memory overhead, more GC pressure
    /// - Reallocation: Expensive for frequent small commands
    /// </summary>
    /// <param name="consumed">Number of bytes to remove from the beginning</param>
    public void ShiftBuffer(int consumed)
    {
        // Guard: Nothing to shift
        if (consumed <= 0) return;

        // Calculate how many bytes remain after removing consumed bytes
        int remaining = BytesRead - consumed;

        if (remaining > 0)
        {
            // Move the remaining bytes to the start of the buffer
            // Array.Copy handles overlapping source/destination correctly
            Array.Copy(
                sourceArray: ReadBuffer,
                sourceIndex: consumed,        // Start of remaining data
                destinationArray: ReadBuffer,
                destinationIndex: 0,          // Beginning of buffer
                length: remaining
            );
        }

        // Update the count of valid bytes in the buffer
        BytesRead = remaining;
    }

    /// <summary>
    /// Closes the connection gracefully.
    ///
    /// Shutdown Sequence:
    /// 1. Socket.Shutdown(Both) - Signals intent to close to the remote end
    ///    - Sends FIN packet
    ///    - Allows graceful TCP teardown
    /// 2. Socket.Close() - Releases OS resources
    ///
    /// Error Handling:
    /// - Shutdown may throw if socket is already closed
    /// - We catch and ignore these errors (connection is closing anyway)
    /// - Close() is called regardless to ensure resource cleanup
    ///
    /// Called When:
    /// - Client disconnects (orderly shutdown)
    /// - Read error occurs (broken connection)
    /// - Idle timeout expires
    /// - Server shutdown
    /// </summary>
    public void Close()
    {
        try
        {
            // Attempt graceful shutdown (may throw if already closed)
            Socket.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // Ignore errors - we're closing anyway
        }

        // Always close the socket to release OS resources
        Socket.Close();
    }

    /// <summary>
    /// High-performance buffer for outgoing response data.
    ///
    /// Performance Optimization - ArrayBufferWriter<byte>:
    /// Replaces List<byte> with specialized buffer writer for zero-copy, zero-allocation writes.
    ///
    /// Why ArrayBufferWriter is Superior:
    /// - Zero-Copy: Direct memory access via Span<byte>, no intermediate arrays
    /// - Zero-Allocation: ResetWrittenCount() is O(1), no memory zeroing like List.Clear()
    /// - IBufferWriter Interface: Standard .NET interface for high-performance serialization
    /// - Efficient Growth: Intelligent resizing without copying old data unnecessarily
    ///
    /// Performance Comparison (10K requests/sec):
    /// - List<byte>: 10K allocations/sec from ToArray() + Clear() = GC pressure
    /// - ArrayBufferWriter: 0 allocations = no GC pauses
    ///
    /// Usage Pattern:
    /// 1. Command handler writes via ResponseWriter.WriteXxx(connection.Writer, ...)
    /// 2. Data accumulates in internal buffer
    /// 3. Flush() sends WrittenSpan (zero-copy)
    /// 4. ResetWrittenCount() clears without zeroing memory (O(1))
    ///
    /// Initial Capacity: 1KB
    /// - Sufficient for most commands (GET, SET, DEL responses)
    /// - Grows automatically for large responses (KEYS, ZRANGE)
    /// - Memory reused across requests (no shrinking)
    /// </summary>
    private readonly ArrayBufferWriter<byte> _writeBuffer = new ArrayBufferWriter<byte>(1024);

    /// <summary>
    /// Exposes the write buffer as IBufferWriter for zero-allocation serialization.
    /// Used by ResponseWriter to write responses directly without intermediate allocations.
    /// </summary>
    public IBufferWriter<byte> Writer => _writeBuffer;

    /// <summary>
    /// Gets the number of bytes written to the write buffer.
    /// Used to check if there's pending data to send and for partial send tracking.
    /// </summary>
    public int WrittenCount => _writeBuffer.WrittenCount;

    /// <summary>
    /// Tracks how many bytes have been successfully sent from the write buffer.
    ///
    /// Used for handling partial sends on non-blocking sockets:
    /// - Initial value: 0 (nothing sent yet)
    /// - After partial send: Set to number of bytes sent
    /// - On complete send: Reset to 0
    ///
    /// Example:
    /// - Write buffer has 1000 bytes
    /// - First Send() sends 700 bytes -> WriteBufferOffset = 700
    /// - Next Send() sends remaining 300 -> WriteBufferOffset = 1000
    /// - Reset buffer -> WriteBufferOffset = 0
    ///
    /// Performance:
    /// - Avoids reallocating buffer for partial sends
    /// - Enables resumable sends without data loss
    /// - Used by NetworkServer's write monitoring (POLLOUT)
    /// </summary>
    public int WriteBufferOffset { get; set; } = 0;

    /// <summary>
    /// Sends buffered response data to the client (supports partial sends).
    ///
    /// Non-Blocking Write Architecture:
    /// This method is designed for event-driven, non-blocking I/O:
    /// - Returns true if all data sent (buffer can be reset)
    /// - Returns false if partial send (needs write monitoring via Socket.Select)
    /// - Never blocks the main thread waiting for slow clients
    ///
    /// Performance Optimization - ArrayBufferWriter.WrittenSpan:
    /// Uses WrittenSpan for zero-copy access to buffered data.
    /// No allocations, no copying - just direct memory access.
    ///
    /// Why This Matters (C10K Scenario):
    /// - Old approach (List<byte>.ToArray()): 10,000 requests/sec = 10,000 allocations = GC pressure
    /// - New approach (ArrayBufferWriter): Zero allocations = no GC pauses
    ///
    /// Partial Send Handling:
    /// When kernel send buffer is full (slow client or network congestion):
    /// 1. Send() returns bytes sent (may be less than requested)
    /// 2. Update WriteBufferOffset to track progress
    /// 3. Return false to signal NetworkServer: "Add me to write monitoring"
    /// 4. NetworkServer uses Socket.Select() to wait for POLLOUT (write-ready)
    /// 5. When ready, calls Flush() again to resume sending
    ///
    /// Example Flow:
    /// Command: MGET key1 key2 ... key1000 (response = 1MB)
    /// - First Flush(): Send 64KB (kernel buffer limit), offset = 64KB, return false
    /// - NetworkServer adds socket to pendingWrites HashSet
    /// - Event loop: Socket.Select() monitors write-ready
    /// - Kernel buffer drains -> Select() signals write-ready
    /// - Second Flush(): Send next 64KB, offset = 128KB, return false
    /// - ... (repeat)
    /// - Final Flush(): Send last chunk, offset = 1MB, reset buffer, return true
    ///
    /// Error Handling:
    /// - SocketException: Client disconnected or network error
    /// - Don't call Close() here - NetworkServer handles cleanup
    /// - Reset buffer to prevent stale data
    ///
    /// Protocol Behavior:
    /// - Redis protocol expects immediate responses
    /// - Partial sends maintain order (TCP guarantees in-order delivery)
    /// - Client sees seamless response (doesn't know about partial sends)
    /// </summary>
    /// <returns>True if all data sent (done), false if needs more writes (partial send)</returns>
    public bool Flush()
    {
        int writtenCount = _writeBuffer.WrittenCount;

        // Check if already sent everything
        if (WriteBufferOffset >= writtenCount)
        {
            // Nothing to send (initial call with empty buffer, or all sent in previous calls)
            if (writtenCount == 0)
            {
                return true;
            }

            // All data sent, reset buffer (O(1) operation - no memory zeroing)
            _writeBuffer.ResetWrittenCount();
            WriteBufferOffset = 0;
            return true;
        }

        try
        {
            // Zero-copy access to written data via ReadOnlySpan
            // This avoids any allocations or copying
            ReadOnlySpan<byte> writtenData = _writeBuffer.WrittenSpan;

            // Create a slice starting from where we left off (for partial sends)
            ReadOnlySpan<byte> remaining = writtenData.Slice(WriteBufferOffset);

            // Send as much as the kernel buffer allows (non-blocking)
            // May send less than requested if kernel send buffer is full
            int sent = Socket.Send(remaining, SocketFlags.None);

            if (sent == 0)
            {
                // Socket closed or unavailable (shouldn't happen on non-blocking socket)
                throw new SocketException((int)SocketError.ConnectionReset);
            }

            // Update progress
            WriteBufferOffset += sent;

            // Check if we've sent everything
            if (WriteBufferOffset >= writtenCount)
            {
                // Reset buffer for next command (O(1) - no memory clearing)
                _writeBuffer.ResetWrittenCount();
                WriteBufferOffset = 0;
                return true; // Done - all data sent
            }

            // Partial send - need to wait for write-ready
            return false; // Not done - still have data to send
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
        {
            // Non-blocking socket: kernel send buffer is full, need to wait
            Console.WriteLine("[Flush] WouldBlock - kernel buffer full, needs write monitoring");
            return false; // Not done - retry when socket is write-ready
        }
        catch (Exception ex)
        {
            // Send failed (network error, client disconnected, etc.)
            // Log the error but DON'T call Close() here
            // The NetworkServer will detect the broken connection on the next read
            // and properly clean up via HandleDisconnect()
            Console.WriteLine($"[Flush Error] {ex.GetType().Name}: {ex.Message}");

            // Reset the buffer to avoid sending stale data if connection recovers
            _writeBuffer.ResetWrittenCount();
            WriteBufferOffset = 0;
            return true; // Treat as "done" to prevent retry on broken connection
        }
    }
}
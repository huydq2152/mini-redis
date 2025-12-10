using System.Net.Sockets;

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
    /// Dynamic buffer for outgoing response data.
    ///
    /// Why List<byte> instead of fixed array?
    /// - Response sizes vary widely (1 byte for Nil, thousands for large arrays)
    /// - List<byte> grows as needed, avoiding waste for small responses
    /// - Automatic capacity doubling provides amortized O(1) append
    ///
    /// Usage Pattern:
    /// 1. Command handler writes response using ResponseWriter methods
    /// 2. Response bytes are appended to this buffer
    /// 3. After command completes, Flush() sends everything
    /// 4. Buffer is cleared for next command
    ///
    /// Memory Management:
    /// - Capacity grows but doesn't shrink (intentional - reuses memory)
    /// - For connections with large responses, capacity stays large
    /// - This is fine - idle connections get closed, freeing memory
    ///
    /// Alternative: Could use ArrayPool<byte> for more control over allocations
    /// </summary>
    public List<byte> WriteBuffer { get; } = new List<byte>();

    /// <summary>
    /// Sends all buffered response data to the client and clears the buffer.
    ///
    /// Called after each command execution to send the response immediately.
    /// This implements "eager flushing" - responses go out as soon as ready.
    ///
    /// Protocol Behavior:
    /// - Redis protocol expects immediate responses for each command
    /// - Buffering responses across commands would break the protocol
    /// - Exception: Pipelined commands still respond in order, just immediately
    ///
    /// Blocking vs Non-Blocking:
    /// - Socket is non-blocking, but we assume Send() completes
    /// - For small responses (<64KB), this is typically true
    /// - For very large responses, Socket.Send() may return partial send
    /// - Current implementation doesn't handle partial sends (simplified)
    ///
    /// Error Handling:
    /// - If Send() fails, we close the connection
    /// - This handles network errors, disconnected clients, etc.
    /// - Closed connections are cleaned up by the event loop
    ///
    /// Future Enhancement:
    /// - Track send progress for large responses
    /// - Use Socket.Send() return value to handle partial sends
    /// - Add write buffer to Select() for flow control
    /// </summary>
    public void Flush()
    {
        // Nothing to send
        if (WriteBuffer.Count == 0) return;

        try
        {
            // Send all buffered data to the client
            // Note: This may not send everything for very large buffers
            // but our responses are typically small (<4KB)
            Socket.Send(WriteBuffer.ToArray());

            // Clear the buffer for the next response
            // Note: This doesn't shrink capacity, which is intentional
            WriteBuffer.Clear();
        }
        catch
        {
            // Send failed (network error, client disconnected, etc.)
            // Close the connection - it will be cleaned up by the event loop
            Close();
        }
    }
}
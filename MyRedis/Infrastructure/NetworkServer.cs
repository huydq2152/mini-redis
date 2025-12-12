using System.Net;
using System.Net.Sockets;
using MyRedis.Abstractions;
using MyRedis.Core;

namespace MyRedis.Infrastructure;

/// <summary>
/// Manages TCP networking and connection lifecycle using non-blocking Socket.Select().
///
/// Responsibility: Network I/O and Connection Management
/// - Accept new client connections
/// - Read data from clients (non-blocking)
/// - Write responses to clients
/// - Detect disconnections and errors
/// - Close idle or dead connections
///
/// Network Architecture:
/// MyRedis uses a select()-based event loop, similar to Redis itself:
/// 1. Socket.Select() waits for events on all sockets
/// 2. When a socket becomes readable:
///    - If it's the listener: Accept new connection
///    - If it's a client: Read data into connection buffer
/// 3. Return list of connections that received data to orchestrator
/// 4. Orchestrator processes commands and sends responses
///
/// Why Select Instead of Threads-Per-Connection?
/// - Scalability: Can handle thousands of connections with one thread
/// - Efficiency: No context switching overhead
/// - Simplicity: Single-threaded = no locking needed
/// - Performance: Modern Redis uses similar approach
///
/// Socket Management:
/// - _listener: The main TCP socket that accepts new connections
/// - _allSockets: List of ALL sockets (listener + all clients)
/// - _connections: Map from client Socket to Connection object
/// - _connectionManager: Tracks last activity time for idle detection
///
/// Connection Lifecycle:
/// 1. Client connects -> HandleAccept() creates new Connection
/// 2. Client sends data -> HandleRead() reads into buffer
/// 3. Client disconnects (gracefully) -> HandleRead() gets 0 bytes -> HandleDisconnect()
/// 4. Client disconnects (error) -> Exception in HandleRead() -> HandleDisconnect()
/// 5. Idle timeout -> BackgroundTaskManager -> CloseIdleConnections()
///
/// Non-Blocking I/O:
/// All sockets are set to non-blocking mode:
/// - Accept(): Returns immediately if no pending connections
/// - Receive(): Returns immediately with available data (no waiting)
/// - Select(): Only reports sockets that are actually ready
///
/// This prevents any operation from blocking the event loop.
///
/// Buffer Management:
/// Each connection has its own buffers:
/// - ReadBuffer: Fixed 4KB for incoming data
/// - WriteBuffer: Dynamic list for outgoing responses
/// - Buffers are managed by Connection class, not NetworkServer
///
/// Error Handling:
/// - Accept error: Log and continue (don't crash server)
/// - Read error: Disconnect client and cleanup
/// - Socket exceptions: Always cleanup to prevent resource leaks
///
/// Performance Considerations:
/// - Zero-copy where possible (read directly into connection buffers)
/// - Minimal allocations (reuse connection buffers)
/// - Efficient select() (only includes active sockets)
/// - Fast lookup (Dictionary for Socket -> Connection mapping)
///
/// Design Pattern: Reactor Pattern
/// - Reactor: Socket.Select() waits for events
/// - Dispatcher: ProcessNetworkEvents() dispatches to handlers
/// - Handlers: HandleAccept(), HandleRead(), HandleDisconnect()
/// </summary>
public class NetworkServer
{
    // The main TCP listening socket that accepts new client connections
    private readonly Socket _listener;

    // List of ALL sockets (listener + all connected clients)
    // Used by Socket.Select() to wait for events on any socket
    private readonly List<Socket> _allSockets = new();

    // Maps client sockets to their Connection objects
    // Connection stores buffers, last activity, etc.
    private readonly Dictionary<Socket, Connection> _connections = new();

    // Tracks connection activity for idle detection
    private readonly IConnectionManager _connectionManager;

    // HashSet of sockets with pending writes (partial sends)
    // Performance: O(1) add/remove, O(K) iteration where K = pending writes count
    // Why HashSet instead of List.Contains or flag iteration:
    // - List.Remove(socket) = O(N) where N = total connections (bad for C10K)
    // - Iterating all connections to check flag = O(N) every event loop (bad for C10K)
    // - HashSet operations = O(1), iteration = O(K) where K << N typically (optimal)
    //
    // Example: 10,000 connections, 50 have pending writes (slow clients)
    // - HashSet: Build writeList = O(50), remove = O(1) = Fast
    // - Flag iteration: Check 10,000 flags every loop = Slow
    private readonly HashSet<Socket> _pendingWrites = new();

    /// <summary>
    /// Creates and configures the network server.
    ///
    /// Setup Process:
    /// 1. Create TCP listener socket
    /// 2. Set socket options (ReuseAddress for fast restart)
    /// 3. Bind to IP address and port
    /// 4. Start listening for connections
    /// 5. Set non-blocking mode
    ///
    /// Socket Options:
    /// - ReuseAddress: Allows immediate rebind after server restart
    ///   Without this, you'd get "Address already in use" for 60s after shutdown
    ///
    /// Listen Backlog:
    /// - 128: Maximum pending connections before accepting
    ///   If 128 clients try to connect simultaneously, they all succeed
    ///   If the 129th connects, it fails (connection refused)
    ///
    /// Non-Blocking Mode:
    /// - Critical for select()-based event loop
    /// - Accept/Receive never block, return immediately
    ///   Ready -> return data/connection
    ///   Not ready -> throw WouldBlock exception
    ///
    /// Why IPAddress.Any?
    /// - Listens on ALL network interfaces (localhost, LAN, etc.)
    /// - Clients can connect via 127.0.0.1, machine IP, etc.
    /// </summary>
    /// <param name="connectionManager">Service to track connection activity</param>
    /// <param name="port">TCP port to listen on (default: 6379, same as Redis)</param>
    public NetworkServer(IConnectionManager connectionManager, int port = 6379)
    {
        // Validate dependency
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));

        // Create TCP socket for IPv4
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        // Allow fast restart after shutdown (avoid "Address already in use")
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // Bind to all network interfaces on the specified port
        _listener.Bind(new IPEndPoint(IPAddress.Any, port));

        // Start listening with backlog of 128 pending connections
        _listener.Listen(128);

        // Set to non-blocking mode (critical for event loop)
        _listener.Blocking = false;

        // Add listener to the socket list (Select will monitor it)
        _allSockets.Add(_listener);

        Console.WriteLine($"[Server] Listening on {IPAddress.Any}:{port}");
    }

    /// <summary>
    /// Performs one iteration of the network event loop using Socket.Select().
    ///
    /// This is the heart of the network layer. It waits for socket events and
    /// processes them when they occur.
    ///
    /// Select Operation:
    /// Socket.Select(readList, writeList, errorList, timeout) waits until:
    /// - A socket in readList becomes readable (data available or new connection)
    /// - A socket in writeList becomes writable (can send data without blocking)
    /// - A socket in errorList has an error
    /// - Timeout expires
    ///
    /// After Select returns, the lists are modified to contain ONLY the sockets
    /// that are actually ready.
    ///
    /// Event Handling:
    /// - Listener socket readable -> New connection pending -> HandleAccept()
    /// - Client socket readable -> Data available -> HandleRead()
    /// - Client socket writable -> Can resume partial send -> HandleWrite()
    /// - Any errors -> HandleDisconnect() cleans up
    ///
    /// Write Monitoring (New):
    /// Sockets are added to writeList when they have pending writes (partial sends).
    /// This prevents blocking the main thread when kernel send buffer is full.
    ///
    /// Performance - C10K Scenario:
    /// - Total connections: 10,000
    /// - Pending writes: ~50 (slow clients)
    /// - Build writeList: O(50) from HashSet, not O(10,000) from flag iteration
    /// - Result: Event loop stays responsive even with many idle connections
    ///
    /// Return Value:
    /// Returns list of connections that received data. The orchestrator will:
    /// 1. Parse commands from their buffers
    /// 2. Execute the commands
    /// 3. Send responses
    ///
    /// Timeout Behavior:
    /// - timeoutMicroseconds = 0: Return immediately (non-blocking poll)
    /// - timeoutMicroseconds > 0: Wait up to N microseconds
    /// - No events and timeout expires: Return empty list
    ///
    /// Why Microseconds?
    /// - Socket.Select() uses microseconds for precision
    /// - BackgroundTaskManager calculates timeout in milliseconds
    /// - We convert: milliseconds * 1000 = microseconds
    ///
    /// Performance:
    /// - Select is O(N) where N = number of sockets
    /// - Very efficient for hundreds of connections
    /// - For thousands+, epoll/IOCP would be better (not implemented)
    ///
    /// Example Flow:
    /// 1. Select waits with 500ms timeout
    /// 2. Client1 sends data -> Select returns immediately
    /// 3. readList = [client1], writeList = [client2 (has pending)], errorList = []
    /// 4. HandleRead(client1) reads data into buffer
    /// 5. HandleWrite(client2) resumes partial send
    /// 6. Return [(connection1, bytesRead)]
    /// 7. Orchestrator processes commands from connection1
    /// </summary>
    /// <param name="timeoutMicroseconds">How long to wait for events (0 = non-blocking, -1 = infinite)</param>
    /// <returns>List of (connection, bytesRead) tuples for connections that received data</returns>
    public IList<(Connection connection, int bytesRead)> ProcessNetworkEvents(int timeoutMicroseconds)
    {
        // Create lists of sockets to monitor
        // Select modifies these lists to contain only ready sockets
        var readList = new List<Socket>(_allSockets);
        var errorList = new List<Socket>(_allSockets);

        // Build write list from pending writes HashSet
        // Performance: O(K) where K = number of pending writes (typically K << N)
        // Alternative (bad): Iterate all N connections checking flag = O(N) every loop
        var writeList = new List<Socket>(_pendingWrites);

        // Wait for socket events (blocks until event or timeout)
        // readList: Will contain sockets with data available or new connections
        // writeList: Will contain sockets ready to send more data (kernel buffer available)
        // errorList: Will contain sockets with errors
        Socket.Select(readList, writeList, errorList, timeoutMicroseconds);

        // List of connections that received data (return value)
        var results = new List<(Connection, int)>();

        // Process all sockets that became readable
        foreach (var socket in readList)
        {
            if (socket == _listener)
            {
                // Listener socket is readable -> new connection pending
                HandleAccept();
            }
            else
            {
                // Client socket is readable -> data available
                var bytesRead = HandleRead(socket);

                // If we successfully read data, add to results
                if (bytesRead > 0 && _connections.TryGetValue(socket, out var connection))
                {
                    results.Add((connection, bytesRead));
                }
            }
        }

        // Process all sockets that became writable (can resume partial sends)
        foreach (var socket in writeList)
        {
            if (_connections.TryGetValue(socket, out var connection))
            {
                // Try to send remaining data
                bool allSent = connection.Flush();

                if (allSent)
                {
                    // All data sent, stop monitoring for writes
                    _pendingWrites.Remove(socket);
                }
            }
        }

        // Note: We could also process errorList here, but HandleRead()
        // already catches exceptions and calls HandleDisconnect()

        // Return connections that have data to process
        return results;
    }

    /// <summary>
    /// Accepts a new client connection.
    ///
    /// Called when the listener socket becomes readable, indicating a client
    /// is trying to connect.
    ///
    /// Process:
    /// 1. Accept the connection (get new client socket)
    /// 2. Set client socket to non-blocking mode
    /// 3. Create Connection object (manages buffers and state)
    /// 4. Add to tracking structures (_allSockets, _connections, _connectionManager)
    ///
    /// Why Non-Blocking?
    /// - Consistent with listener socket
    /// - Receive() never blocks the event loop
    /// - Returns immediately with available data
    ///
    /// Connection Tracking:
    /// - _allSockets: So Select() monitors this client for events
    /// - _connections: So we can find Connection object from Socket
    /// - _connectionManager: So we can detect idle timeout
    ///
    /// Error Handling:
    /// - If Accept() fails, log and continue (don't crash server)
    /// - Server keeps running even if one Accept() fails
    ///
    /// Typical Errors:
    /// - Too many file descriptors (OS limit reached)
    /// - Network error during handshake
    /// - Client disconnected during Accept()
    /// </summary>
    private void HandleAccept()
    {
        try
        {
            // Accept the pending connection (get new client socket)
            var client = _listener.Accept();

            // Set to non-blocking mode (must be non-blocking for event loop)
            client.Blocking = false;

            // Add to socket list so Select() monitors it
            _allSockets.Add(client);

            // Create Connection object (buffers, state, etc.)
            var connection = new Connection(client);

            // Map socket to connection for fast lookup
            _connections[client] = connection;

            // Track in connection manager for idle detection
            _connectionManager.Add(connection);

            Console.WriteLine($"[New Conn] {client.RemoteEndPoint}");
        }
        catch (Exception ex)
        {
            // Accept failed, but server continues running
            // Possible causes: OS limits, network errors, etc.
            Console.WriteLine($"[Accept Error] {ex.Message}");
        }
    }

    /// <summary>
    /// Reads data from a client socket into the connection buffer.
    ///
    /// Called when Select() reports that a client socket has data available.
    ///
    /// Process:
    /// 1. Update connection's last activity time (for idle detection)
    /// 2. Read data from socket into connection.ReadBuffer
    /// 3. If 0 bytes read: Client disconnected gracefully
    /// 4. If exception: Client disconnected with error
    /// 5. If success: Return bytes read
    ///
    /// Buffer Management:
    /// - connection.ReadBuffer: Fixed 4KB array
    /// - connection.BytesRead: How many bytes currently in buffer
    /// - We read into: ReadBuffer[BytesRead...4096]
    /// - This appends new data to existing data
    ///
    /// Example:
    /// - Buffer has 10 bytes: [data...][empty...]
    /// - Receive() reads into buffer[10...4096]
    /// - Receive() returns 20 (read 20 bytes)
    /// - Update BytesRead: 10 + 20 = 30
    /// - Buffer now has 30 bytes: [data...data...][empty...]
    ///
    /// Graceful Disconnect:
    /// - Client calls close() on their socket
    /// - Our Receive() returns 0 bytes
    /// - This is normal shutdown, not an error
    ///
    /// Error Disconnect:
    /// - Network cable unplugged
    /// - Client crashes
    /// - TCP RST received
    /// - Receive() throws exception
    ///
    /// Activity Tracking:
    /// - ConnectionManager.Touch() updates last activity time
    /// - This resets the idle timer
    /// - If client sends data, they're not idle
    ///
    /// Why Touch Before Receive?
    /// - Even attempting to read shows the connection is active
    /// - If Receive fails, we disconnect anyway
    /// </summary>
    /// <param name="clientSocket">The socket to read from</param>
    /// <returns>Number of bytes read (0 = disconnected, -1 = error/disconnected)</returns>
    private int HandleRead(Socket clientSocket)
    {
        // Look up connection object for this socket
        if (!_connections.TryGetValue(clientSocket, out var conn))
            return 0; // Socket not tracked (shouldn't happen)

        // Update last activity time (resets idle timer)
        _connectionManager.Touch(conn);

        try
        {
            // Check if buffer is full before reading
            // This prevents the "4KB Wall" deadlock where:
            // 1. Buffer is full (BytesRead == Length)
            // 2. Receive(buffer, offset, size=0) returns 0
            // 3. Server mistakenly thinks client disconnected
            //
            // When buffer is full, there are two possibilities:
            // A) Parse will succeed (command complete) -> ShiftBuffer frees space
            // B) Parse will fail (command incomplete) -> Need to grow buffer
            //
            // We grow the buffer preemptively when full.
            // If parse succeeds, next read will use the larger buffer (no harm).
            // If parse fails, we've already grown (prevents deadlock).
            if (conn.BytesRead == conn.ReadBuffer.Length)
            {
                // Buffer is full - try to grow it
                if (!conn.GrowBuffer())
                {
                    // Growth failed: Exceeded MAX_BUFFER_SIZE (512MB)
                    // This is a protocol error - command is too large
                    Console.WriteLine($"[Protocol Error] Command exceeds maximum size ({Connection.MaxBufferSize} bytes)");
                    HandleDisconnect(clientSocket);
                    return 0;
                }
                // Buffer successfully grown - continue to read
            }

            // Read data from socket into connection buffer
            // Parameters:
            // - buffer: The byte array to read into
            // - offset: Where in the array to start writing (conn.BytesRead)
            // - size: Maximum bytes to read (remaining space in buffer)
            // - flags: None (no special behavior)
            var bytesRead = clientSocket.Receive(
                conn.ReadBuffer,                         // Destination buffer
                conn.BytesRead,                          // Start position (append to existing data)
                conn.ReadBuffer.Length - conn.BytesRead, // Space remaining
                SocketFlags.None);

            if (bytesRead == 0)
            {
                // Graceful disconnect: Client closed their side
                // This is normal shutdown (not an error)
                HandleDisconnect(clientSocket);
                return 0;
            }

            // Update how many valid bytes are in the buffer
            conn.BytesRead += bytesRead;

            // Return bytes read (success)
            return bytesRead;
        }
        catch (Exception ex)
        {
            // Error during read: Connection lost
            // Possible causes: Network error, client crash, RST packet, etc.
            Console.WriteLine($"Error: {ex.Message}");
            HandleDisconnect(clientSocket);
            return 0;
        }
    }

    /// <summary>
    /// Cleans up a disconnected client connection.
    ///
    /// Called when:
    /// - Client disconnects gracefully (Receive returns 0)
    /// - Client disconnects with error (Receive throws exception)
    /// - Server closes idle connection (CloseIdleConnections)
    ///
    /// Cleanup Process:
    /// 1. Remove from connection manager (stop tracking idle time)
    /// 2. Close the socket (TCP FIN sent to client)
    /// 3. Remove from _connections dictionary
    /// 4. Remove from _allSockets list (Select no longer monitors it)
    /// 5. Remove from _pendingWrites (stop write monitoring)
    ///
    /// Why This Order?
    /// - ConnectionManager first: It may access the Connection object
    /// - Close socket: Release OS resources (file descriptor)
    /// - Remove from dictionaries: Release managed memory
    /// - Remove from tracking sets: Prevent stale references
    ///
    /// Resource Cleanup:
    /// This prevents resource leaks by ensuring:
    /// - Socket file descriptor released (OS limit is ~1000-65000)
    /// - Connection object can be garbage collected
    /// - No references remain in tracking structures
    ///
    /// Idempotency:
    /// - TryGetValue ensures safe double-cleanup
    /// - If connection already removed, does nothing
    /// - This is safe even if called multiple times
    ///
    /// Why Log RemoteEndPoint?
    /// - Shows which client disconnected
    /// - Helpful for debugging connection issues
    /// - May be null if socket already disposed (that's OK)
    /// </summary>
    /// <param name="socket">The client socket to disconnect and cleanup</param>
    private void HandleDisconnect(Socket socket)
    {
        // Look up connection for this socket
        if (_connections.TryGetValue(socket, out var conn))
        {
            // Log the disconnect (shows which client)
            Console.WriteLine($"[Disconnected] {socket.RemoteEndPoint}");

            // Remove from connection manager (stop idle tracking)
            _connectionManager.Remove(conn);

            // Close the socket and release OS resources
            // This sends TCP FIN to client (graceful shutdown)
            conn.Close();

            // Remove from connection dictionary
            _connections.Remove(socket);
        }

        // Remove from socket list (Select will no longer monitor it)
        _allSockets.Remove(socket);

        // Remove from pending writes (if it was waiting for write-ready)
        // HashSet.Remove() is O(1) and idempotent (safe even if not present)
        _pendingWrites.Remove(socket);
    }

    /// <summary>
    /// Registers a socket for write monitoring (POLLOUT).
    ///
    /// Called after command processing when Flush() indicates partial send:
    /// - Flush() returns false -> data still in WriteBuffer -> needs write monitoring
    /// - Socket added to _pendingWrites HashSet
    /// - Next ProcessNetworkEvents() includes this socket in writeList
    /// - Select() waits for kernel send buffer to have space
    /// - When ready, Flush() is called again to resume sending
    ///
    /// Performance:
    /// - HashSet.Add() is O(1)
    /// - Idempotent: Adding same socket multiple times is safe
    ///
    /// Use Case:
    /// - Large responses (1MB+ from MGET, ZRANGE, etc.)
    /// - Slow clients (network congestion, limited bandwidth)
    /// - High throughput scenarios (kernel buffer saturation)
    ///
    /// Why Not Called from Flush() Directly?
    /// - Flush() is in Connection class (domain model)
    /// - _pendingWrites is in NetworkServer (infrastructure)
    /// - Separation of concerns: Connection shouldn't know about NetworkServer
    /// - Orchestrator coordinates between the two
    /// </summary>
    /// <param name="socket">The socket that has pending writes</param>
    public void RegisterPendingWrite(Socket socket)
    {
        if (_pendingWrites.Add(socket))
        {
            Console.WriteLine($"[Pending Write] {socket.RemoteEndPoint} registered for write monitoring");
        }
    }

    /// <summary>
    /// Closes connections that have exceeded the idle timeout.
    ///
    /// Called by BackgroundTaskManager when idle connections are detected.
    ///
    /// Process:
    /// For each idle connection:
    /// 1. Log the closure (shows which client timed out)
    /// 2. Call HandleDisconnect() to cleanup
    ///
    /// Why Separate Method?
    /// - BackgroundTaskManager detects idle connections
    /// - But it doesn't manage sockets (not its responsibility)
    /// - This method bridges the gap: idle detection -> network cleanup
    ///
    /// Idle Timeout:
    /// - Default: 5 minutes (300 seconds)
    /// - Starts counting from last data received
    /// - Sending responses doesn't reset timer (only receiving data)
    ///
    /// Why Close Idle Connections?
    /// - Client may have crashed without closing socket
    /// - Network may have partitioned (client unreachable)
    /// - Connection pools may forget to close connections
    /// - Prevents resource exhaustion (sockets, memory, tracking structures)
    ///
    /// Client Prevention:
    /// Well-behaved clients should either:
    /// 1. Close connections when done (preferred)
    /// 2. Send periodic PING commands (keepalive)
    /// 3. Use short-lived connections (connect, command, disconnect)
    ///
    /// Performance:
    /// - Typically 0 connections to close (clients behave well)
    /// - When cleanup needed, very fast (just socket closure)
    /// </summary>
    /// <param name="idleConnections">List of connections to close (from ConnectionManager)</param>
    public void CloseIdleConnections(IList<Connection> idleConnections)
    {
        foreach (var conn in idleConnections)
        {
            // Log which connection is being closed for timeout
            Console.WriteLine($"[Idle] Closing idle connection {conn.Socket.RemoteEndPoint}");

            // Perform full cleanup (remove from all tracking, close socket)
            HandleDisconnect(conn.Socket);
        }
    }
}
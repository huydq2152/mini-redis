using System.Net.Sockets;
using System.Text;
using System.Buffers.Binary;

namespace MyRedis.Client
{
    /// <summary>
    /// A simple Redis client implementation that communicates with the MyRedis server
    /// using a custom binary protocol.
    ///
    /// Protocol Format:
    /// Request: [4-byte arg count][4-byte length][string data][4-byte length][string data]...
    /// Response: [1-byte type][type-specific data]
    ///
    /// This client is designed for testing the MyRedis server implementation.
    /// </summary>
    public class SimpleRedisClient : IDisposable
    {
        // TCP client for network communication
        private readonly TcpClient _client;

        // Network stream for reading/writing data over the TCP connection
        private readonly NetworkStream _stream;

        /// <summary>
        /// Creates a new Redis client and connects to the specified server.
        /// </summary>
        /// <param name="host">Server hostname or IP address (e.g., "127.0.0.1" or "localhost")</param>
        /// <param name="port">Server port number (default Redis port is 6379)</param>
        /// <exception cref="SocketException">Thrown if connection fails</exception>
        public SimpleRedisClient(string host, int port)
        {
            // Create a new TCP client instance
            _client = new TcpClient();

            // Connect to the Redis server at the specified host and port
            // This is a blocking call that will wait until connection succeeds or fails
            _client.Connect(host, port);

            // Get the network stream for reading and writing data
            _stream = _client.GetStream();

            Console.WriteLine($"[Client] Connected to {host}:{port}");
        }

        /// <summary>
        /// Sends a Redis command to the server using the binary protocol.
        ///
        /// Protocol Structure:
        /// [4 bytes: argument count]
        /// [4 bytes: length of arg1][arg1 bytes]
        /// [4 bytes: length of arg2][arg2 bytes]
        /// ...
        ///
        /// Example: SendCommand("SET", "name", "John")
        /// Will send: [3][3]["SET"][4]["name"][4]["John"]
        /// where numbers in brackets are 4-byte little-endian integers.
        /// </summary>
        /// <param name="args">Command and its arguments (e.g., "SET", "key", "value")</param>
        public void SendCommand(params string[] args)
        {
            // Guard: Don't send empty commands
            if (args.Length == 0) return;

            // Use MemoryStream to buffer the entire packet before sending
            // This ensures the packet is sent as a single unit, which:
            // 1. Avoids partial sends that could confuse the parser
            // 2. Enables testing of pipelining (multiple commands in one TCP packet)
            using var ms = new MemoryStream();

            // Allocate a small stack buffer for writing integers (4 bytes)
            // Using stackalloc is more efficient than heap allocation for small buffers
            Span<byte> intBuffer = stackalloc byte[4];

            // Step 1: Write the argument count as a 4-byte little-endian unsigned integer
            // Little-endian means the least significant byte comes first
            // Example: 3 becomes [03 00 00 00] in memory
            BinaryPrimitives.WriteUInt32LittleEndian(intBuffer, (uint)args.Length);
            ms.Write(intBuffer);

            // Step 2: For each argument, write its length followed by its content
            foreach (var arg in args)
            {
                // Convert the string argument to UTF-8 bytes
                // UTF-8 is used for compatibility with Redis and international characters
                byte[] strBytes = Encoding.UTF8.GetBytes(arg);

                // Write the length of this string (4 bytes, little-endian)
                BinaryPrimitives.WriteUInt32LittleEndian(intBuffer, (uint)strBytes.Length);
                ms.Write(intBuffer);

                // Write the actual string content (variable length)
                ms.Write(strBytes);
            }

            // Convert the MemoryStream to a byte array and send it all at once
            // This atomic send operation helps with pipelining support:
            // - The server can receive multiple commands in a single TCP packet
            // - The protocol parser can process them sequentially from the buffer
            var packet = ms.ToArray();
            _stream.Write(packet);
        }

        /// <summary>
        /// Reads and prints a response from the Redis server.
        ///
        /// Response Protocol:
        /// [1 byte: type code][type-specific data]
        ///
        /// Type codes:
        /// 0 = Nil (no data follows)
        /// 1 = Error (error message follows)
        /// 2 = String ([4 bytes: length][string data])
        /// 3 = Integer ([8 bytes: int64 value])
        /// 4 = Array ([4 bytes: count][elements...])
        /// </summary>
        public void ReadAndPrintResponse()
        {
            // Read the first byte which indicates the response type
            byte[] typeBuf = new byte[1];

            // If we can't read even 1 byte, the connection is probably closed
            if (_stream.Read(typeBuf, 0, 1) == 0) return;

            // Process the response based on its type code
            switch (typeBuf[0])
            {
                case 0:
                    // Type 0: Nil - represents null/non-existent values
                    // Used when a key doesn't exist (e.g., GET on missing key)
                    Console.WriteLine("(nil)");
                    break;

                case 1:
                    // Type 1: Error - command execution failed
                    // The server encountered an error (wrong args, wrong type, etc.)
                    Console.WriteLine("(err)");
                    break;

                case 2:
                    // Type 2: String - variable-length string value
                    // Format: [4 bytes length][UTF-8 string data]
                    // Used for GET responses, simple replies like "OK", etc.
                    Console.WriteLine($"(str) {ReadString()}");
                    break;

                case 3:
                    // Type 3: Integer - 64-bit signed integer
                    // Format: [8 bytes int64 little-endian]
                    // Used for counts, TTL values, etc.
                    Console.WriteLine($"(int) {ReadInt64()}");
                    break;

                case 4:
                    // Type 4: Array - list of values
                    // Format: [4 bytes count][element1][element2]...
                    // Used for KEYS, ZRANGE, and other multi-value responses
                    // TODO: Implement recursive array parsing for full support
                    Console.WriteLine("(arr)");
                    break;

                default:
                    // Unknown type - this shouldn't happen with a correct server
                    Console.WriteLine($"Unknown type: {typeBuf[0]}");
                    break;
            }
        }

        /// <summary>
        /// Reads a string value from the network stream.
        ///
        /// Format: [4 bytes: length][UTF-8 string data]
        ///
        /// This is used for Type 2 (String) responses.
        /// </summary>
        /// <returns>The decoded UTF-8 string</returns>
        private string ReadString()
        {
            // Read the 4-byte length prefix
            byte[] lenBuf = new byte[4];
            _stream.Read(lenBuf, 0, 4);

            // Convert the 4 bytes to an int32 (little-endian format)
            // BitConverter respects the system's endianness, which is little-endian on x86/x64
            int len = BitConverter.ToInt32(lenBuf, 0);

            // Read the actual string data based on the length we just read
            byte[] data = new byte[len];
            _stream.Read(data, 0, len);

            // Decode the UTF-8 bytes to a C# string
            return Encoding.UTF8.GetString(data);
        }

        /// <summary>
        /// Reads a 64-bit integer value from the network stream.
        ///
        /// Format: [8 bytes: int64 little-endian]
        ///
        /// This is used for Type 3 (Integer) responses.
        /// Examples: DEL (number of keys deleted), TTL (seconds remaining), etc.
        /// </summary>
        /// <returns>The 64-bit signed integer value</returns>
        private long ReadInt64()
        {
            // Read 8 bytes for the int64 value
            byte[] buf = new byte[8];
            _stream.Read(buf, 0, 8);

            // Convert the 8 bytes to a long (int64) in little-endian format
            return BitConverter.ToInt64(buf, 0);
        }

        /// <summary>
        /// Disposes of the client resources (network stream and TCP client).
        /// This should be called when you're done with the client to properly close the connection.
        ///
        /// Using 'using var client = new SimpleRedisClient(...)' will automatically call this.
        /// </summary>
        public void Dispose()
        {
            // Dispose the network stream first (closes the stream)
            _stream.Dispose();

            // Then dispose the TCP client (closes the socket and releases resources)
            _client.Dispose();
        }
    }
}
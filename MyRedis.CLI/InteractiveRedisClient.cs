using System.Net.Sockets;
using System.Text;
using System.Buffers.Binary;

namespace MyRedis.CLI
{
    /// <summary>
    /// An interactive Redis client that provides a CLI interface similar to redis-cli.
    /// Supports sending commands and receiving formatted responses including arrays.
    ///
    /// Protocol Format:
    /// Request: [4-byte arg count][4-byte length][string data][4-byte length][string data]...
    /// Response: [1-byte type][type-specific data]
    ///
    /// Response Types:
    /// 0 = Nil (null value)
    /// 1 = Error (error message)
    /// 2 = String (variable-length string)
    /// 3 = Integer (64-bit signed integer)
    /// 4 = Array (variable-length array of values)
    /// </summary>
    public class InteractiveRedisClient : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        /// <summary>
        /// Creates a new interactive Redis client and connects to the specified server.
        /// </summary>
        /// <param name="host">Server hostname or IP address (e.g., "127.0.0.1")</param>
        /// <param name="port">Server port number (default 6379)</param>
        public InteractiveRedisClient(string host, int port)
        {
            _client = new TcpClient();
            _client.Connect(host, port);
            _stream = _client.GetStream();
        }

        /// <summary>
        /// Parses a command string into arguments and sends it to the server.
        /// Handles quoted strings to support arguments with spaces.
        ///
        /// Examples:
        /// - "SET name John" -> ["SET", "name", "John"]
        /// - "SET name \"John Doe\"" -> ["SET", "name", "John Doe"]
        /// </summary>
        /// <param name="commandLine">The command line to parse and send</param>
        public void SendCommand(string commandLine)
        {
            // Parse the command line into arguments
            var args = ParseCommandLine(commandLine);
            if (args.Length == 0) return;

            // Build the binary protocol packet
            using var ms = new MemoryStream();
            Span<byte> intBuffer = stackalloc byte[4];

            // Write argument count
            BinaryPrimitives.WriteUInt32LittleEndian(intBuffer, (uint)args.Length);
            ms.Write(intBuffer);

            // Write each argument with its length prefix
            foreach (var arg in args)
            {
                byte[] strBytes = Encoding.UTF8.GetBytes(arg);
                BinaryPrimitives.WriteUInt32LittleEndian(intBuffer, (uint)strBytes.Length);
                ms.Write(intBuffer);
                ms.Write(strBytes);
            }

            // Send the packet
            var packet = ms.ToArray();
            _stream.Write(packet);
        }

        /// <summary>
        /// Parses a command line string into an array of arguments.
        /// Supports quoted strings with spaces and escape sequences.
        ///
        /// Examples:
        /// - "SET key value" -> ["SET", "key", "value"]
        /// - "SET key \"hello world\"" -> ["SET", "key", "hello world"]
        /// - "SET key 'hello world'" -> ["SET", "key", "hello world"]
        /// </summary>
        private string[] ParseCommandLine(string commandLine)
        {
            var args = new List<string>();
            var currentArg = new StringBuilder();
            bool inQuotes = false;
            char quoteChar = '\0';

            for (int i = 0; i < commandLine.Length; i++)
            {
                char c = commandLine[i];

                if (!inQuotes && (c == '"' || c == '\''))
                {
                    // Start of quoted string
                    inQuotes = true;
                    quoteChar = c;
                }
                else if (inQuotes && c == quoteChar)
                {
                    // End of quoted string
                    inQuotes = false;
                    quoteChar = '\0';
                }
                else if (!inQuotes && char.IsWhiteSpace(c))
                {
                    // Whitespace outside quotes - end of argument
                    if (currentArg.Length > 0)
                    {
                        args.Add(currentArg.ToString());
                        currentArg.Clear();
                    }
                }
                else
                {
                    // Regular character - add to current argument
                    currentArg.Append(c);
                }
            }

            // Add the last argument if any
            if (currentArg.Length > 0)
            {
                args.Add(currentArg.ToString());
            }

            return args.ToArray();
        }

        /// <summary>
        /// Reads and returns a formatted response from the server.
        /// Supports all response types including nested arrays.
        /// </summary>
        /// <returns>Formatted response string, or null if connection closed</returns>
        public string? ReadResponse()
        {
            // Read response type byte
            byte[] typeBuf = new byte[1];
            if (_stream.Read(typeBuf, 0, 1) == 0)
            {
                return null; // Connection closed
            }

            return ReadResponseValue(typeBuf[0], indent: 0);
        }

        /// <summary>
        /// Recursively reads a response value based on its type.
        /// Supports nested arrays with proper indentation.
        /// </summary>
        /// <param name="type">Response type code (0-4)</param>
        /// <param name="indent">Current indentation level for pretty printing</param>
        /// <returns>Formatted string representation of the value</returns>
        private string ReadResponseValue(byte type, int indent)
        {
            string indentStr = new string(' ', indent * 2);

            switch (type)
            {
                case 0:
                    // Type 0: Nil - represents null/non-existent values
                    return "(nil)";

                case 1:
                    // Type 1: Error - command execution failed
                    return "(error)";

                case 2:
                    // Type 2: String - variable-length string value
                    return $"\"{ReadString()}\"";

                case 3:
                    // Type 3: Integer - 64-bit signed integer
                    return $"(integer) {ReadInt64()}";

                case 4:
                    // Type 4: Array - list of values (potentially nested)
                    return ReadArray(indent);

                default:
                    return $"(unknown type: {type})";
            }
        }

        /// <summary>
        /// Reads an array response with support for nested arrays.
        /// Each element is printed on its own line with proper indentation.
        /// </summary>
        /// <param name="indent">Current indentation level</param>
        /// <returns>Formatted array string</returns>
        private string ReadArray(int indent)
        {
            // Read array length
            byte[] countBuf = new byte[4];
            _stream.Read(countBuf, 0, 4);
            int count = BitConverter.ToInt32(countBuf, 0);

            if (count == 0)
            {
                return "(empty array)";
            }

            var result = new StringBuilder();
            string indentStr = new string(' ', indent * 2);

            // Read each element
            for (int i = 0; i < count; i++)
            {
                // Read element type
                byte[] typeBuf = new byte[1];
                _stream.Read(typeBuf, 0, 1);

                // Format with index number (1-based like redis-cli)
                if (i > 0) result.AppendLine();
                result.Append($"{indentStr}{i + 1}) ");

                // Read and append the element value
                string value = ReadResponseValue(typeBuf[0], indent + 1);

                // For nested arrays, the value already contains newlines and indentation
                // For simple values, just append directly
                if (typeBuf[0] == 4) // Array type
                {
                    result.AppendLine();
                    result.Append(indentStr + "  " + value);
                }
                else
                {
                    result.Append(value);
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Reads a string value from the network stream.
        /// Format: [4 bytes: length][UTF-8 string data]
        /// </summary>
        private string ReadString()
        {
            // Read length prefix
            byte[] lenBuf = new byte[4];
            _stream.Read(lenBuf, 0, 4);
            int len = BitConverter.ToInt32(lenBuf, 0);

            // Read string data
            byte[] data = new byte[len];
            _stream.Read(data, 0, len);

            return Encoding.UTF8.GetString(data);
        }

        /// <summary>
        /// Reads a 64-bit integer value from the network stream.
        /// Format: [8 bytes: int64 little-endian]
        /// </summary>
        private long ReadInt64()
        {
            byte[] buf = new byte[8];
            _stream.Read(buf, 0, 8);
            return BitConverter.ToInt64(buf, 0);
        }

        /// <summary>
        /// Disposes of the client resources.
        /// </summary>
        public void Dispose()
        {
            _stream.Dispose();
            _client.Dispose();
        }
    }
}

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
    ///
    /// Pipelining Support:
    /// Supports Redis-style pipelining for high-throughput scenarios:
    /// - Queue multiple commands without waiting for responses
    /// - Send all commands in a single TCP packet
    /// - Receive all responses in batch
    /// - Dramatically improves throughput (10x-100x faster for bulk operations)
    ///
    /// Usage Modes:
    /// 1. Interactive Mode (default): Send command → Wait for response
    /// 2. Pipeline Mode: Queue commands → EXEC → Batch responses
    /// 3. File Pipeline Mode: Load commands from file → Batch execute
    /// </summary>
    public class InteractiveRedisClient : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        /// <summary>
        /// Pipeline mode: Queue for commands waiting to be sent in batch.
        /// When in pipeline mode, commands are collected here instead of sent immediately.
        /// </summary>
        private readonly List<string> _pipelineQueue = new();

        /// <summary>
        /// Indicates whether the client is in pipeline mode.
        /// In pipeline mode, commands are queued instead of sent immediately.
        /// </summary>
        public bool InPipelineMode { get; private set; } = false;

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
        /// Pipelining Behavior:
        /// - If InPipelineMode=false: Sends command immediately (default)
        /// - If InPipelineMode=true: Queues command for batch execution
        ///
        /// Examples:
        /// - "SET name John" -> ["SET", "name", "John"]
        /// - "SET name \"John Doe\"" -> ["SET", "name", "John Doe"]
        /// </summary>
        /// <param name="commandLine">The command line to parse and send</param>
        /// <returns>True if command was sent/queued successfully</returns>
        public bool SendCommand(string commandLine)
        {
            // Parse the command line into arguments
            var args = ParseCommandLine(commandLine);
            if (args.Length == 0) return false;

            // If in pipeline mode, queue the command instead of sending
            if (InPipelineMode)
            {
                _pipelineQueue.Add(commandLine);
                Console.WriteLine("Queued.");
                return true;
            }

            // Normal mode: Send command immediately
            SendCommandImmediate(args);
            return true;
        }

        /// <summary>
        /// Sends a single command immediately to the server.
        /// Used for both normal mode and pipeline execution.
        /// </summary>
        /// <param name="args">Parsed command arguments</param>
        private void SendCommandImmediate(string[] args)
        {
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
        /// Enters pipeline mode where commands are queued instead of sent immediately.
        ///
        /// Usage:
        /// > PIPELINE
        /// Entering pipeline mode. Commands will be queued. Type EXEC to send all.
        /// pipeline> SET key1 value1
        /// Queued.
        /// pipeline> GET key1
        /// Queued.
        /// pipeline> EXEC
        /// Executing 2 commands...
        /// 1) OK
        /// 2) "value1"
        /// </summary>
        public void EnterPipelineMode()
        {
            if (InPipelineMode)
            {
                Console.WriteLine("Already in pipeline mode.");
                return;
            }

            InPipelineMode = true;
            _pipelineQueue.Clear();
            Console.WriteLine("Entering pipeline mode. Commands will be queued.");
            Console.WriteLine("Type EXEC to send all commands, or DISCARD to cancel.");
        }

        /// <summary>
        /// Exits pipeline mode and discards all queued commands.
        /// </summary>
        public void DiscardPipeline()
        {
            if (!InPipelineMode)
            {
                Console.WriteLine("Not in pipeline mode.");
                return;
            }

            int count = _pipelineQueue.Count;
            InPipelineMode = false;
            _pipelineQueue.Clear();
            Console.WriteLine($"Pipeline discarded. {count} command(s) removed.");
        }

        /// <summary>
        /// Executes all queued commands in a single batch (pipelining).
        ///
        /// Performance:
        /// - Sends all commands in one TCP packet (reduces network round-trips)
        /// - Server processes commands sequentially
        /// - Receives all responses in batch
        /// - Typical speedup: 10x-100x faster than individual commands
        ///
        /// Algorithm:
        /// 1. Send all queued commands in one packet
        /// 2. Read responses for each command
        /// 3. Display results with numbering
        /// </summary>
        /// <returns>Number of commands executed</returns>
        public int ExecutePipeline()
        {
            if (!InPipelineMode)
            {
                Console.WriteLine("Not in pipeline mode. Use PIPELINE to enter.");
                return 0;
            }

            if (_pipelineQueue.Count == 0)
            {
                Console.WriteLine("No commands queued.");
                InPipelineMode = false;
                return 0;
            }

            int commandCount = _pipelineQueue.Count;
            Console.WriteLine($"Executing {commandCount} command(s) in pipeline...\n");

            // Build a single packet with all commands
            using var ms = new MemoryStream();

            foreach (var commandLine in _pipelineQueue)
            {
                var args = ParseCommandLine(commandLine);
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
            }

            // Send the entire batch in one packet
            var packet = ms.ToArray();
            _stream.Write(packet);
            _stream.Flush();

            // Read responses for each command
            for (int i = 0; i < commandCount; i++)
            {
                Console.WriteLine($"{i + 1}) {ReadResponse()}");
            }

            // Clean up and exit pipeline mode
            InPipelineMode = false;
            _pipelineQueue.Clear();

            Console.WriteLine($"\nPipeline completed: {commandCount} command(s) executed.");
            return commandCount;
        }

        /// <summary>
        /// Loads and executes commands from a file in pipeline mode.
        ///
        /// File Format:
        /// Each line is a separate command:
        /// SET key1 value1
        /// SET key2 value2
        /// GET key1
        /// GET key2
        ///
        /// Blank lines and lines starting with # are ignored (comments).
        ///
        /// Performance:
        /// Same as ExecutePipeline() - all commands sent in one batch.
        /// </summary>
        /// <param name="filePath">Path to file containing commands</param>
        /// <returns>Number of commands executed, or -1 on error</returns>
        public int ExecuteFromFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Error: File not found: {filePath}");
                return -1;
            }

            Console.WriteLine($"Loading commands from {filePath}...");

            // Read and parse file
            var commands = new List<string>();
            foreach (var line in File.ReadAllLines(filePath))
            {
                var trimmed = line.Trim();

                // Skip empty lines and comments
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
                    continue;

                commands.Add(trimmed);
            }

            if (commands.Count == 0)
            {
                Console.WriteLine("No valid commands found in file.");
                return 0;
            }

            Console.WriteLine($"Loaded {commands.Count} command(s). Executing pipeline...\n");

            // Build a single packet with all commands
            using var ms = new MemoryStream();

            foreach (var commandLine in commands)
            {
                var args = ParseCommandLine(commandLine);
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
            }

            // Send the entire batch in one packet
            var packet = ms.ToArray();
            _stream.Write(packet);
            _stream.Flush();

            // Read responses for each command
            for (int i = 0; i < commands.Count; i++)
            {
                Console.WriteLine($"{i + 1}) {ReadResponse()}");
            }

            Console.WriteLine($"\nFile pipeline completed: {commands.Count} command(s) executed.");
            return commands.Count;
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
                    // Format: [Type 1][4-byte code][4-byte msg len][UTF-8 message]
                    // CRITICAL: Must read the error code and message to consume them from the stream!
                    // If we don't, they'll corrupt the next response.
                    return ReadError();

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
        /// Reads an error response from the network stream.
        /// Format: [4 bytes: error code][4 bytes: message length][UTF-8 message]
        ///
        /// CRITICAL: This method MUST read all error data to prevent stream corruption.
        /// If we skip reading the error message, it will remain in the stream and
        /// corrupt the next response.
        /// </summary>
        private string ReadError()
        {
            // Read error code
            byte[] codeBuf = new byte[4];
            _stream.Read(codeBuf, 0, 4);
            int errorCode = BitConverter.ToInt32(codeBuf, 0);

            // Read message length
            byte[] lenBuf = new byte[4];
            _stream.Read(lenBuf, 0, 4);
            int messageLength = BitConverter.ToInt32(lenBuf, 0);

            // Read error message
            byte[] msgData = new byte[messageLength];
            _stream.Read(msgData, 0, messageLength);
            string errorMessage = Encoding.UTF8.GetString(msgData);

            // Format like redis-cli: (error) MESSAGE
            return $"(error) {errorMessage}";
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

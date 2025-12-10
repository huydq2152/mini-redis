using MyRedis.Client;

// ============================================================================
// MyRedis Client Test Program
// ============================================================================
// This program tests the MyRedis server implementation by sending various
// Redis commands and verifying the server's behavior.
//
// Prerequisites:
// - The MyRedis server must be running on localhost:6379
// - Run this after starting the server with: dotnet run --project MyRedis
// ============================================================================

Console.WriteLine("--- Starting C# Redis Client Test ---");

try
{
    // Create a client connection to the Redis server
    // The 'using' statement ensures the connection is properly closed when done
    using var client = new SimpleRedisClient("127.0.0.1", 6379);

    // ========================================================================
    // Test 1: SET Command
    // ========================================================================
    // Tests basic key-value storage
    // SET command syntax: SET <key> <value>
    // Expected server behavior: Store "Tuan" under key "name"
    // Note: We don't read the response here since the server may not send one
    //       for SET commands (fire-and-forget for this test)
    Console.WriteLine("\n[1] Sending: SET name Tuan");
    client.SendCommand("SET", "name", "Tuan");

    // ========================================================================
    // Test 2: GET Command
    // ========================================================================
    // Tests retrieving a stored value
    // GET command syntax: GET <key>
    // Expected response: Type 2 (String) with value "Tuan"
    Console.WriteLine("\n[2] Sending: GET name");
    client.SendCommand("GET", "name");

    // ========================================================================
    // Test 3: Pipelining
    // ========================================================================
    // Tests the server's ability to handle multiple commands in rapid succession
    //
    // Pipelining is a Redis feature where multiple commands can be sent without
    // waiting for individual responses. This improves performance by reducing
    // round-trip latency.
    //
    // How it works:
    // 1. Client sends multiple commands back-to-back
    // 2. TCP's Nagle algorithm may combine them into a single packet
    // 3. Server's parser must correctly split and process each command
    // 4. Responses are sent back in the same order
    //
    // This test validates that the ProtocolParser can handle multiple commands
    // arriving in a single buffer and process them sequentially.
    Console.WriteLine("\n[3] Sending Pipelined: PING + ECHO hello");

    // Send PING command (should respond with "PONG" or similar)
    client.SendCommand("PING");

    // Immediately send ECHO command without waiting for PING response
    // ECHO syntax: ECHO <message>
    // Expected response: Type 2 (String) with value "hello world"
    client.SendCommand("ECHO", "hello world");
    
    // ========================================================================
    // Test 4-7: Sorted Set (ZSET) Operations
    // ========================================================================
    // Sorted sets are one of Redis's advanced data structures
    // Each element has a score (number) and the set maintains sorted order
    //
    // Internal implementation in MyRedis:
    // - Dictionary for O(1) score lookup by member name
    // - AVL tree for O(log n) insertion and range queries in sorted order
    //
    // Use cases: leaderboards, priority queues, range queries by score

    // Add UserA with score 100
    // ZADD syntax: ZADD <key> <score> <member>
    Console.WriteLine("\n[4] Sending: ZADD myzset 100 UserA");
    client.SendCommand("ZADD", "myzset", "100", "UserA");

    // Add UserB with score 50 (lowest score - will be first in sorted order)
    Console.WriteLine("\n[5] Sending: ZADD myzset 50 UserB");
    client.SendCommand("ZADD", "myzset", "50", "UserB");

    // Add UserC with score 150 (highest score - will be last in sorted order)
    Console.WriteLine("\n[6] Sending: ZADD myzset 150 UserC");
    client.SendCommand("ZADD", "myzset", "150", "UserC");

    // Retrieve all members in sorted order (by score, ascending)
    // ZRANGE syntax: ZRANGE <key> <start> <stop>
    // 0 = first element, -1 = last element (like Python list slicing)
    // Expected order: UserB (50) -> UserA (100) -> UserC (150)
    // Expected response: Type 4 (Array) containing the three member names
    Console.WriteLine("\n[7] Sending: ZRANGE myzset 0 -1");
    client.SendCommand("ZRANGE", "myzset", "0", "-1");
    
    // ========================================================================
    // Test 8: Time-To-Live (TTL) and Expiration
    // ========================================================================
    // Redis supports automatic key expiration after a specified time
    // This is useful for caching, session management, and temporary data
    //
    // Implementation details:
    // - ExpirationManager maintains a min-heap of (expiry_time, key) pairs
    // - BackgroundTaskManager checks for expired keys in the event loop
    // - Expired keys are automatically deleted from the data store

    Console.WriteLine("\n[8] Testing TTL");

    // Set a key that we'll expire
    client.SendCommand("SET", "temp", "I will die soon");

    // Set expiration time to 2 seconds from now
    // EXPIRE syntax: EXPIRE <key> <seconds>
    // Expected response: Type 3 (Integer) with value 1 (success)
    client.SendCommand("EXPIRE", "temp", "2");

    // Immediately try to get the key (should still exist)
    // Expected response: Type 2 (String) with value "I will die soon"
    Console.WriteLine("Getting temp immediately...");
    client.SendCommand("GET", "temp");

    // Wait for the key to expire (2s expiry + 1s buffer = 3s total)
    Console.WriteLine("Waiting 3s...");
    Thread.Sleep(3000);

    // Try to get the key again (should be expired and deleted)
    // Expected response: Type 0 (Nil) indicating the key no longer exists
    Console.WriteLine("Getting temp again...");
    client.SendCommand("GET", "temp");

    // ========================================================================
    // Test Complete
    // ========================================================================
    Console.WriteLine("\nTests finished. Check Server Console output!");
    Console.WriteLine("\nWhat to verify on the server:");
    Console.WriteLine("1. All commands were parsed and executed correctly");
    Console.WriteLine("2. Pipelined commands (PING + ECHO) were handled in sequence");
    Console.WriteLine("3. ZRANGE returned members in sorted order (UserB, UserA, UserC)");
    Console.WriteLine("4. Key 'temp' was automatically deleted after expiration");
}
catch (Exception ex)
{
    // Connection or communication error occurred
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine("Make sure the Server is running!");
    Console.WriteLine("\nTo start the server:");
    Console.WriteLine("  dotnet run --project MyRedis/MyRedis.csproj");
}
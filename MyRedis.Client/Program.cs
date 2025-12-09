using MyRedis.Client;

Console.WriteLine("--- Starting C# Redis Client Test ---");

try
{
    using var client = new SimpleRedisClient("127.0.0.1", 6379);

    // Test 1: Lệnh SET
    Console.WriteLine("\n[1] Sending: SET name Tuan");
    client.SendCommand("SET", "name", "Tuan");
    // (Server chưa phản hồi nên ta không Read ở đây, tránh bị treo)

    // Test 2: Lệnh GET
    Console.WriteLine("\n[2] Sending: GET name");
    client.SendCommand("GET", "name");

    // Test 3: Pipelining (Gửi 2 lệnh dính liền nhau cực nhanh)
    // Server phải đủ thông minh để tách chúng ra.
    Console.WriteLine("\n[3] Sending Pipelined: PING + ECHO hello");
    
    // Lưu ý: Hàm SendCommand của chúng ta gửi ngay lập tức.
    // Để giả lập Pipelining (2 lệnh trong 1 gói TCP), ta có thể viết tay hoặc gọi liên tiếp.
    // Do Nagle Algorithm của TCP, gọi liên tiếp 2 lần write thường sẽ được gộp packet.
    // Hoặc ta sửa lại Client để hỗ trợ Batch Send. Nhưng ở mức đơn giản, gọi liên tiếp là đủ test Parser.
    
    client.SendCommand("PING");
    client.SendCommand("ECHO", "hello world");
    
    Console.WriteLine("\n[4] Sending: ZADD myzset 100 UserA");
    client.SendCommand("ZADD", "myzset", "100", "UserA");

    Console.WriteLine("\n[5] Sending: ZADD myzset 50 UserB");
    client.SendCommand("ZADD", "myzset", "50", "UserB"); // UserB điểm thấp hơn

    Console.WriteLine("\n[6] Sending: ZADD myzset 150 UserC");
    client.SendCommand("ZADD", "myzset", "150", "UserC");
    
    // Lấy Top 3 người thấp đến cao (UserB -> UserA -> UserC)
    Console.WriteLine("\n[7] Sending: ZRANGE myzset 0 -1");
    client.SendCommand("ZRANGE", "myzset", "0", "-1");
    
    Console.WriteLine("\n[8] Testing TTL");
    client.SendCommand("SET", "temp", "I will die soon");
    client.SendCommand("EXPIRE", "temp", "2"); // Hết hạn sau 2s

    Console.WriteLine("Getting temp immediately...");
    client.SendCommand("GET", "temp"); // Nên trả về value

    Console.WriteLine("Waiting 3s...");
    Thread.Sleep(3000);

    Console.WriteLine("Getting temp again...");
    client.SendCommand("GET", "temp"); // Nên trả về (nil)

    Console.WriteLine("\nTests finished. Check Server Console output!");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine("Make sure the Server is running!");
}
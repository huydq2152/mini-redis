using System.Net;
using System.Net.Sockets;
using MyRedis.Storage.DataStructures;
using MyRedis.System;

namespace MyRedis.Core;

public class RedisServer
{
    private readonly Socket _listener;
    private const int Port = 6379;

    // Danh sách tất cả socket để đưa vào hàm Select()
    private readonly List<Socket> _allSockets = new List<Socket>();

    // Map từ Socket -> Connection Object (để lưu trạng thái)
    private readonly Dictionary<Socket, Connection> _connections = new Dictionary<Socket, Connection>();

    private readonly Dictionary<string, object?> _store = new Dictionary<string, object?>();

    private readonly ExpirationManager _ttlManager = new ExpirationManager();
    private readonly IdleManager _idleManager = new IdleManager();

    private readonly BackgroundWorker _bgWorker = new BackgroundWorker();

    // Ngưỡng để quyết định xem việc có "nặng" không (Ví dụ: > 1000 phần tử)
    private const int LargeCollectionThreshold = 1000;

    public RedisServer()
    {
        // 1. Khởi tạo Socket (Socket() syscall) [cite: 99]
        _listener =
            new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        // 2. Set Options (SO_REUSEADDR) [cite: 107]
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress,
            true);

        // 3. Bind & Listen [cite: 111-124]
        _listener.Bind(
            new IPEndPoint(IPAddress.Any, Port));
        _listener.Listen(128);

        // QUAN TRỌNG: Chuyển sang Non-blocking mode [cite: 447-462]
        _listener.Blocking =
            false;

        _allSockets.Add(_listener);
        Console.WriteLine($"[Server] Listening on {IPAddress.Any}:{Port}");
    }

    public void Run()
    {
        while (true)
        {
            // 1. TÍNH TOÁN TIMEOUT [cite: 2407-2420]
            // Server chỉ được phép ngủ đến khi sự kiện sớm nhất xảy ra (TTL hoặc Idle)
            int ttlWait = _ttlManager.GetNextTimeout();

            _ttlManager.GetNextTimeout();
            int idleWait = _idleManager.GetNextTimeout();
            int selectTimeout = Math.Min(ttlWait, idleWait);

            // Chuyển sang microsecond cho Socket.Select
            if (selectTimeout < 0) selectTimeout = 0;
            int selectMicroSeconds = selectTimeout * 1000;

            // 2. IO MULTIPLEXING
            var readList = new List<Socket>(_allSockets);
            var errorList = new List<Socket>(_allSockets);

            Socket.Select(readList, null, errorList, selectMicroSeconds);

            // 3. XỬ LÝ SỰ KIỆN MẠNG
            // ... (Logic HandleAccept, HandleDisconnect cũ) ...

            foreach (var socket in readList)
            {
                if (socket == _listener) HandleAccept();
                else HandleRead(socket);
            }

            // 4. XỬ LÝ TÁC VỤ NỀN (BACKGROUND TASKS) [cite: 2389, 2686]
            ProcessBackgroundTasks();
        }
    }

    private void ProcessBackgroundTasks()
    {
        // A. Xóa Key hết hạn
        var expiredKeys = _ttlManager.ProcessExpiredKeys();
        foreach (var key in expiredKeys)
        {
            _store.Remove(key);
            Console.WriteLine($"[TTL] Expired key: {key}");
        }

        // B. Ngắt kết nối nhàn rỗi
        var idleConns = _idleManager.GetIdleConnections();
        foreach (var conn in idleConns)
        {
            Console.WriteLine($"[Idle] Closing idle connection {conn.Socket.RemoteEndPoint}");
            HandleDisconnect(conn.Socket); // Tái sử dụng hàm ngắt kết nối
        }
    }

    private void HandleAccept()
    {
        try
        {
            // Accept connection mới
            Socket client = _listener.Accept();

            // Set non-blocking cho client mới [cite: 588]
            client.Blocking =
                false;

            // Thêm vào danh sách quản lý
            _allSockets.Add(client);
            _connections[client] = new Connection(client);

            Console.WriteLine($"[New Conn] {client.RemoteEndPoint}");

            var conn = new Connection(client);
            _connections[client] = conn;
            _idleManager.Add(conn);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Accept Error] {ex.Message}");
        }
    }

    private void HandleRead(Socket clientSocket)
    {
        var conn = _connections[clientSocket];
        _idleManager.Touch(conn);
        try
        {
            // Đọc tiếp vào vị trí BytesRead đang đứng
            int bytesRead = clientSocket.Receive(
                conn.ReadBuffer,
                conn.BytesRead,
                conn.ReadBuffer.Length - conn.BytesRead,
                SocketFlags.None);

            if (bytesRead == 0)
            {
                HandleDisconnect(clientSocket);
                return;
            }

            conn.BytesRead += bytesRead;

            // Vòng lặp xử lý (Pipelining): 
            // Client có thể gửi nhiều lệnh cùng lúc (Command 1 + Command 2...)
            // Ta phải parse lần lượt cho đến khi hết dữ liệu
            while (true)
            {
                if (ProtocolParser.TryParse(conn.ReadBuffer, conn.BytesRead, out var cmd, out int consumed))
                {
                    // Parse thành công 1 lệnh!
                    Console.WriteLine($"[Command] {string.Join(" ", cmd)}");

                    ExecuteCommand(conn, cmd);

                    // Gửi phản hồi ngay lập tức (Flush)
                    conn.Flush();

                    // Xóa lệnh đã xử lý khỏi buffer (memmove)
                    conn.ShiftBuffer(consumed);
                }
                else
                {
                    // Chưa đủ dữ liệu cho 1 lệnh trọn vẹn. 
                    // Thoát vòng lặp, chờ lần Select() tiếp theo đọc thêm data.
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            HandleDisconnect(clientSocket);
        }
    }

    private void HandleDisconnect(Socket socket)
    {
        // Dọn dẹp tài nguyên [cite: 548-550]
        if (_connections.ContainsKey(socket))
        {
            Console.WriteLine($"[Disconnected] {socket.RemoteEndPoint}");
            var conn = _connections[socket];
            _idleManager.Remove(conn);
            conn.Close();
            _connections.Remove(socket);
        }

        _allSockets.Remove(socket);
    }

    // Router điều hướng lệnh
    private void ExecuteCommand(Connection conn, List<string> cmd)
    {
        if (cmd.Count == 0) return;

        string commandName = cmd[0].ToUpper();

        switch (commandName)
        {
            case "GET":
                HandleGet(conn, cmd);
                break;
            case "SET":
                HandleSet(conn, cmd);
                break;
            case "DEL":
                HandleDel(conn, cmd);
                break;
            case "KEYS":
                HandleKeys(conn, cmd); // Bonus từ Chapter 9
                break;
            case "PING": // Lệnh test huyền thoại
                ResponseWriter.WriteString(conn.WriteBuffer, "PONG");
                break;
            case "ECHO":
                if (cmd.Count > 1) ResponseWriter.WriteString(conn.WriteBuffer, cmd[1]);
                else ResponseWriter.WriteError(conn.WriteBuffer, 1, "Missing arg");
                break;
            case "ZADD":
                HandleZAdd(conn, cmd);
                break;
            case "ZRANGE":
                HandleZRange(conn, cmd);
                break;
            case "EXPIRE":
                HandleExpire(conn, cmd);
                break;
            case "TTL":
                HandleTTL(conn, cmd);
                break;
            default:
                ResponseWriter.WriteError(conn.WriteBuffer, 1, "Unknown cmd");
                break;
        }
    }

    // --- CÁC HÀM XỬ LÝ LOGIC ---

    private void HandleSet(Connection conn, List<string> cmd)
    {
        if (cmd.Count < 3)
        {
            ResponseWriter.WriteError(conn.WriteBuffer, 1, "ERR wrong number of arguments");
            return;
        }

        _store[cmd[1]] = cmd[2];
        ResponseWriter.WriteNil(conn.WriteBuffer); // Set trả về Nil (hoặc OK tùy phiên bản)
    }

    private void HandleGet(Connection conn, List<string> cmd)
    {
        // Logic Lazy Expiration
        string key = cmd[1];
        if (_ttlManager.IsExpired(key)) // Kiểm tra xem hết hạn chưa
        {
            _store.Remove(key); // Xóa ngay nếu hết hạn
            // Không cần xóa trong Heap, để ProcessExpiredKeys dọn sau cũng được
        }

        if (cmd.Count < 2)
        {
            ResponseWriter.WriteError(conn.WriteBuffer, 1, "ERR wrong number of arguments");
            return;
        }

        if (_store.TryGetValue(cmd[1], out var value) && value is string stringValue)
        {
            ResponseWriter.WriteString(conn.WriteBuffer, stringValue);
        }
        else
        {
            ResponseWriter.WriteNil(conn.WriteBuffer); // Not found
        }
    }

    private void HandleDel(Connection conn, List<string> cmd)
    {
        if (cmd.Count < 2)
        {
            ResponseWriter.WriteError(conn.WriteBuffer, 1, "ERR wrong number of arguments");
            return;
        }

        string key = cmd[1];
        if (_store.TryGetValue(key, out object val))
        {
            // 1. Gỡ khỏi Dictionary ngay lập tức (Luồng chính làm - O(1))
            _store.Remove(key);
            _ttlManager.RemoveExpiration(key);

            // 2. Quyết định cách hủy (Destroy)
            if (IsLargeObject(val))
            {
                // Nặng -> Gửi xuống Background (Async Unlink)
                Console.WriteLine($"[Async] Unlinking large key: {key}");
                _bgWorker.Submit(() => DestroyObject(val));
            }
            else
            {
                // Nhẹ -> Hủy luôn (Sync)
                DestroyObject(val);
            }

            ResponseWriter.WriteInt(conn.WriteBuffer, 1);
        }
        else
        {
            ResponseWriter.WriteInt(conn.WriteBuffer, 0);
        }
    }

    private bool IsLargeObject(object val)
    {
        if (val is SortedSet zset)
        {
            // Giả sử ta thêm property Count vào SortedSet
            // return zset.Count > LargeCollectionThreshold;
            // Tạm thời hardcode logic demo:
            return true; // Coi như ZSet nào cũng nặng để test
        }

        return false; // String coi như nhẹ
    }

    private void DestroyObject(object val)
    {
        // Trong C#, GC tự dọn dẹp.
        // Tuy nhiên, với cấu trúc phức tạp như AVL Tree, ta có thể giúp GC
        // bằng cách cắt đứt các tham chiếu (Reference Breaking) để tránh
        // Stack Overflow khi GC quét đệ quy hoặc giảm áp lực Gen 2.

        if (val is SortedSet zset)
        {
            // Giả lập công việc nặng nhọc:
            // Duyệt cây và set null từng node (hoặc chỉ đơn giản là sleep để demo)
            Thread.Sleep(500); // Demo: Giả vờ việc này tốn 500ms
            Console.WriteLine("[BgWorker] Large object destroyed.");
        }
    }

    private void HandleKeys(Connection conn, List<string> cmd)
    {
        // Trả về danh sách toàn bộ Key
        ResponseWriter.WriteArrayHeader(conn.WriteBuffer, _store.Count);
        foreach (var key in _store.Keys)
        {
            ResponseWriter.WriteString(conn.WriteBuffer, key);
        }
    }

    private void HandleZAdd(Connection conn, List<string> cmd)
    {
        // Cú pháp: ZADD key score member
        if (cmd.Count != 4)
        {
            ResponseWriter.WriteError(conn.WriteBuffer, 1, "ERR wrong number of arguments");
            return;
        }

        string key = cmd[1];
        if (!double.TryParse(cmd[2], out double score))
        {
            ResponseWriter.WriteError(conn.WriteBuffer, 1, "ERR value is not a float");
            return;
        }

        string member = cmd[3];

        // Lấy hoặc tạo mới ZSet
        if (!_store.TryGetValue(key, out object val))
        {
            val = new SortedSet();
            _store[key] = val;
        }

        if (val is SortedSet zset)
        {
            bool added = zset.Add(member, score);
            ResponseWriter.WriteInt(conn.WriteBuffer, added ? 1 : 0);
        }
        else
        {
            ResponseWriter.WriteError(conn.WriteBuffer, 1,
                "WRONGTYPE Operation against a key holding the wrong kind of value");
        }
    }

    private void HandleZRange(Connection conn, List<string> cmd)
    {
        // Cú pháp: ZRANGE key start stop
        if (cmd.Count != 4)
        {
            ResponseWriter.WriteError(conn.WriteBuffer, 1, "ERR wrong number of arguments");
            return;
        }

        string key = cmd[1];
        if (!int.TryParse(cmd[2], out int start) || !int.TryParse(cmd[3], out int stop))
        {
            ResponseWriter.WriteError(conn.WriteBuffer, 1, "ERR value is not an integer");
            return;
        }

        if (_store.TryGetValue(key, out object val))
        {
            if (val is SortedSet zset)
            {
                var items = zset.Range(start, stop);

                // Serialize mảng chuỗi trả về
                ResponseWriter.WriteArrayHeader(conn.WriteBuffer, items.Count);
                foreach (var item in items)
                {
                    ResponseWriter.WriteString(conn.WriteBuffer, item);
                }
            }
            else
            {
                ResponseWriter.WriteError(conn.WriteBuffer, 1, "WRONGTYPE");
            }
        }
        else
        {
            // Key không tồn tại -> Trả về mảng rỗng
            ResponseWriter.WriteArrayHeader(conn.WriteBuffer, 0);
        }
    }

    private void HandleExpire(Connection conn, List<string> cmd)
    {
        // EXPIRE key seconds
        if (cmd.Count != 3)
        {
            /* Error */
            return;
        }

        string key = cmd[1];
        if (!int.TryParse(cmd[2], out int sec))
        {
            /* Error */
            return;
        }

        if (_store.ContainsKey(key))
        {
            _ttlManager.SetExpiration(key, sec * 1000);
            ResponseWriter.WriteInt(conn.WriteBuffer, 1);
        }
        else
        {
            ResponseWriter.WriteInt(conn.WriteBuffer, 0);
        }
    }

    private void HandleTTL(Connection conn, List<string> cmd)
    {
        string key = cmd[1];
        if (!_store.ContainsKey(key))
        {
            ResponseWriter.WriteInt(conn.WriteBuffer, -2); // Key not exists
            return;
        }

        long? ttl = _ttlManager.GetTTL(key);
        if (ttl == null)
        {
            ResponseWriter.WriteInt(conn.WriteBuffer, -1); // No TTL
        }
        else
        {
            ResponseWriter.WriteInt(conn.WriteBuffer, ttl.Value / 1000); // Trả về giây
        }
    }
}
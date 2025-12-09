using System.Net.Sockets;

namespace MyRedis.Core;

public class Connection
{
    public Socket Socket { get; }
        
    // Sách dùng: uint8_t rbuf[4 + k_max_msg];
    // C# ta dùng mảng byte. Tạm thời fix cứng 4KB.
    public byte[] ReadBuffer { get; } = new byte[4096];
        
    // Biến này để theo dõi ta đã đọc được bao nhiêu byte vào buffer
    public int BytesRead { get; set; } = 0;
    
    public long LastActive { get; set; } = Environment.TickCount64;
        
    // Node của Linked List (Intrusive-ish)
    // Giúp ta remove connection khỏi giữa list mà không cần duyệt O(N)
    public LinkedListNode<Connection> Node { get; set; }

    public Connection(Socket socket)
    {
        Socket = socket;
    }
    
    // Kỹ thuật "Compact": Dịch chuyển dữ liệu thừa lên đầu
    // Tương đương lệnh `memmove` trong sách Chapter 6
    public void ShiftBuffer(int consumed)
    {
        if (consumed <= 0) return;
            
        int remaining = BytesRead - consumed;
        if (remaining > 0)
        {
            // Copy phần đuôi lên đầu
            Array.Copy(ReadBuffer, consumed, ReadBuffer, 0, remaining);
        }
            
        BytesRead = remaining;
    }
        
    public void Close()
    {
        try { Socket.Shutdown(SocketShutdown.Both); } catch { }
        Socket.Close();
    }
    
    // Buffer cho việc gửi dữ liệu (Output)
    // Dùng List<byte> cho đơn giản ở giai đoạn này (Dynamic resizing)
    public List<byte> WriteBuffer { get; } = new List<byte>();

    public void Flush()
    {
        if (WriteBuffer.Count == 0) return;
            
        // Gửi toàn bộ dữ liệu trong buffer
        // Lưu ý: Trong thực tế non-blocking, Send có thể không gửi hết 1 lần.
        // Nhưng để đơn giản hóa Milestone này, ta giả định gửi hết.
        try 
        {
            Socket.Send(WriteBuffer.ToArray());
            WriteBuffer.Clear();
        }
        catch { Close(); }
    }
}
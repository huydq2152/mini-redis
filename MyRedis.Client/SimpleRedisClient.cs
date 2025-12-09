using System.Net.Sockets;
using System.Text;
using System.Buffers.Binary;

namespace MyRedis.Client
{
    public class SimpleRedisClient : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        public SimpleRedisClient(string host, int port)
        {
            _client = new TcpClient();
            _client.Connect(host, port);
            _stream = _client.GetStream();
            Console.WriteLine($"[Client] Connected to {host}:{port}");
        }

        // Hàm quan trọng nhất: Đóng gói lệnh thành Binary Protocol
        public void SendCommand(params string[] args)
        {
            if (args.Length == 0) return;

            // Dùng MemoryStream để gom toàn bộ gói tin lại trước khi gửi (tránh gửi lắt nhắt)
            using var ms = new MemoryStream();
            
            // 1. Ghi số lượng tham số (nStr) - 4 bytes Little Endian
            // Ta dùng mảng tạm 4 byte để chứa số nguyên
            Span<byte> intBuffer = stackalloc byte[4];
            
            BinaryPrimitives.WriteUInt32LittleEndian(intBuffer, (uint)args.Length);
            ms.Write(intBuffer);

            foreach (var arg in args)
            {
                byte[] strBytes = Encoding.UTF8.GetBytes(arg);

                // 2. Ghi độ dài chuỗi (len)
                BinaryPrimitives.WriteUInt32LittleEndian(intBuffer, (uint)strBytes.Length);
                ms.Write(intBuffer);

                // 3. Ghi nội dung chuỗi
                ms.Write(strBytes);
            }

            // Gửi toàn bộ gói tin qua Socket
            // Việc này đảm bảo tính "Atomic" của gói tin ở mức ứng dụng, 
            // giúp server dễ dàng test tính năng Pipelining (nhận 1 cục to chứa nhiều lệnh)
            var packet = ms.ToArray();
            _stream.Write(packet);
        }

        public void ReadAndPrintResponse()
        {
            // Đọc 1 byte Type
            byte[] typeBuf = new byte[1];
            if (_stream.Read(typeBuf, 0, 1) == 0) return;

            switch (typeBuf[0])
            {
                case 0: Console.WriteLine("(nil)"); break; // Nil
                case 1: Console.WriteLine("(err)"); break; // Err
                case 2: // String [Len][Data]
                    Console.WriteLine($"(str) {ReadString()}"); 
                    break;
                case 3: // Int [8 byte]
                    Console.WriteLine($"(int) {ReadInt64()}");
                    break;
                case 4: // Arr [Len][...]
                    Console.WriteLine("(arr)");
                    // Cần logic đệ quy để in mảng, tạm thời in ra báo hiệu thôi
                    break;
                default: Console.WriteLine($"Unknown type: {typeBuf[0]}"); break;
            }
        }

        private string ReadString()
        {
            byte[] lenBuf = new byte[4];
            _stream.Read(lenBuf, 0, 4);
            int len = BitConverter.ToInt32(lenBuf, 0); // Nhớ handle Little Endian nếu cần
    
            byte[] data = new byte[len];
            _stream.Read(data, 0, len);
            return Encoding.UTF8.GetString(data);
        }

        private long ReadInt64()
        {
            byte[] buf = new byte[8];
            _stream.Read(buf, 0, 8);
            return BitConverter.ToInt64(buf, 0);
        }

        public void Dispose()
        {
            _stream.Dispose();
            _client.Dispose();
        }
    }
}
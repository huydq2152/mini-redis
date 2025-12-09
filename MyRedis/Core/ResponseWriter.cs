using System.Text;
using System.Buffers.Binary;

namespace MyRedis.Core
{
    public enum ResponseType : byte
    {
        Nil = 0,
        Err = 1,
        Str = 2,
        Int = 3,
        Arr = 4
    }

    public static class ResponseWriter
    {
        // Ghi số nguyên 4 byte (Little Endian)
        private static void WriteInt32(List<byte> buffer, int value)
        {
            Span<byte> span = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            buffer.AddRange(span.ToArray());
        }

        // Ghi số nguyên 8 byte (cho kiểu INT)
        private static void WriteInt64(List<byte> buffer, long value)
        {
            Span<byte> span = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(span, value);
            buffer.AddRange(span.ToArray());
        }

        public static void WriteNil(List<byte> buffer)
        {
            buffer.Add((byte)ResponseType.Nil);
        }

        public static void WriteString(List<byte> buffer, string value)
        {
            buffer.Add((byte)ResponseType.Str);
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteInt32(buffer, bytes.Length); // 4 byte độ dài
            buffer.AddRange(bytes);           // Nội dung
        }

        public static void WriteInt(List<byte> buffer, long value)
        {
            buffer.Add((byte)ResponseType.Int);
            WriteInt64(buffer, value);
        }

        public static void WriteError(List<byte> buffer, int code, string message)
        {
            buffer.Add((byte)ResponseType.Err);
            WriteInt32(buffer, code);
            var msgBytes = Encoding.UTF8.GetBytes(message);
            WriteInt32(buffer, msgBytes.Length);
            buffer.AddRange(msgBytes);
        }
        
        // Ghi tiêu đề mảng (Dùng cho lệnh KEYS)
        public static void WriteArrayHeader(List<byte> buffer, int count)
        {
            buffer.Add((byte)ResponseType.Arr);
            WriteInt32(buffer, count);
        }
    }
}
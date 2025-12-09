using System.Buffers.Binary;
using System.Text;

namespace MyRedis.Core;

public static class ProtocolParser
{
    // Hàm này trả về true nếu parse thành công trọn vẹn 1 lệnh.
    // Trả về false nếu thiếu dữ liệu (cần đọc thêm từ socket).
    public static bool TryParse(byte[] buffer, int dataLen, out List<string> command, out int bytesConsumed)
    {
        command = null;
        bytesConsumed = 0;
            
        // Dùng Span để thao tác bộ nhớ an toàn và nhanh
        var span = buffer.AsSpan(0, dataLen);
        int offset = 0;

        // 1. Kiểm tra header tổng (4 byte số lượng chuỗi)
        if (span.Length < 4) return false;
            
        // Đọc số lượng phần tử (nStr)
        uint nStr = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;

        // Giới hạn an toàn (tránh hacker gửi số quá lớn làm tràn RAM)
        if (nStr > 1024) throw new Exception("Protocol Error: Too many arguments");

        var result = new List<string>((int)nStr);

        // 2. Vòng lặp đọc từng phần tử
        for (int i = 0; i < nStr; i++)
        {
            // Kiểm tra xem có đủ 4 byte độ dài không
            if (span.Length - offset < 4) return false;

            uint strLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
            offset += 4;

            // Kiểm tra xem có đủ nội dung chuỗi không
            if (span.Length - offset < strLen) return false;

            // Đọc nội dung chuỗi
            // (Lưu ý: Ở đây ta convert sang string cho dễ, sau này tối ưu sẽ giữ nguyên byte[])
            string str = Encoding.UTF8.GetString(span.Slice(offset, (int)strLen));
            result.Add(str);
                
            offset += (int)strLen;
        }

        command = result;
        bytesConsumed = offset; // Báo lại số byte đã dùng để cắt bỏ khỏi buffer
        return true;
    }
}